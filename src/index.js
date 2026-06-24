// URLのパスを見て、ランキング取得かスコア送信かを振り分けます。
export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    // ルートにアクセスされたらランキングを返します。
    if (url.pathname === "/") {
      return getRanking(env);
    }

    // token付きURLにアクセスされたらランキング登録処理をします。
    if (url.pathname === "/submit-token") {
      return submitToken(url, env);
    }

    // 用意していないURLにアクセスされた場合です。
    return json({
      ok: false,
      error: "not found"
    }, 404);
  }
};

// Unity側とCloudflare側で同じ値にしてください。
// これが違うとtokenを復号・検証できません。
const SECRET = "HYAKURETUKEN"; // 好きな暗号キー(例：ANGOU_KEY)

// tokenを受け取って、復号・検証・D1保存まで行う処理です。
async function submitToken(url, env) {
  try {
    // scoresテーブルがなければ作成します。
    await createTableIfNeeded(env);

    // URLの ?token=... 部分を取得します。
    const token =
      url.searchParams.get("token");

    // tokenが無い場合は登録できません。
    if (!token) {
      return json({
        ok: false,
        error: "token missing"
      }, 400);
    }

    // Unity側で暗号化したtokenを復号します。
    const payload =
      decrypt(token);

    // 復号した文字列から、名前・スコア・日時・署名を取り出します。
    const parsed =
      parsePayload(payload);

    // 形式が壊れている場合は無効です。
    if (!parsed) {
      return json({
        ok: false,
        error: "invalid payload"
      }, 400);
    }

    // サーバー側でも同じ署名を作り直します。
    const expectedSignature =
      createSignature(
        parsed.name,
        parsed.score,
        parsed.time
      );

    // Unity側で作った署名と一致しなければ改ざん扱いにします。
    if (expectedSignature !== parsed.signature) {
      return json({
        ok: false,
        error: "signature invalid"
      }, 400);
    }

    // payload内の日時をD1に保存する形式へ変換します。
    const createdAt =
      formatTimestamp(parsed.time);

    // 既に同じユーザーのスコアがあるか確認します。
    const existing =
      await env.DB.prepare(`
        SELECT score
        FROM scores
        WHERE user_name = ?
      `)
      .bind(parsed.name)
      .first();

    // 初登録ならそのまま保存します。
    if (!existing) {
      await env.DB.prepare(`
        INSERT INTO scores
        (
          user_name,
          score,
          created_at
        )
        VALUES (?, ?, ?)
      `)
      .bind(
        parsed.name,
        parsed.score,
        createdAt
      )
      .run();
    }
    // 既に登録済みの場合は、自己ベストを超えた時だけ更新します。
    else if (parsed.score > existing.score) {
      await env.DB.prepare(`
        UPDATE scores
        SET
          score = ?,
          created_at = ?
        WHERE user_name = ?
      `)
      .bind(
        parsed.score,
        createdAt,
        parsed.name
      )
      .run();
    }

    // 登録後、最新ランキングを返します。
    return getRanking(env);
  }
  catch (e) {
    // 予期しないエラーが起きた時の返答です。
    return json({
      ok: false,
      error: e.toString()
    }, 500);
  }
}

// Unity側でXOR暗号化 + Hex化したtokenを復号します。
function decrypt(hex) {
  let result = "";

  // 2文字ずつHexを数値に戻します。
  for (let i = 0; i < hex.length; i += 2) {
    const value =
      parseInt(
        hex.substring(i, i + 2),
        16
      );

    // Unity側と同じキー文字を取り出します。
    const secretChar =
      SECRET.charCodeAt(
        (i / 2) % SECRET.length
      );

    // XORで元の文字に戻します。
    result +=
      String.fromCharCode(
        value ^ secretChar
      );
  }

  return result;
}

// 復号したpayloadからデータを取り出します。
// 形式: 名前長3桁 + 名前 + スコア8桁 + 時刻12桁 + 署名10桁
function parsePayload(payload) {
  // 最低限必要な長さがなければ無効です。
  if (payload.length < 3 + 8 + 12 + 10) {
    return null;
  }

  // 先頭3桁が名前の長さです。
  const nameLength = parseInt(payload.substring(0, 3), 10);

  // 名前長が変なら無効です。
  if (isNaN(nameLength) || nameLength < 1 || nameLength > 999) {
    return null;
  }

  // payload全体の長さが想定と一致するか確認します。
  const expectedLength = 3 + nameLength + 8 + 12 + 10;

  if (payload.length !== expectedLength) {
    return null;
  }

  // 名前を取り出します。
  const name = payload.substring(3, 3 + nameLength);

  const scoreStart = 3 + nameLength;

  // スコア、時刻、署名を取り出します。
  const score = parseInt(payload.substring(scoreStart, scoreStart + 8), 10);
  const time = payload.substring(scoreStart + 8, scoreStart + 20);
  const signature = parseInt(payload.substring(scoreStart + 20, scoreStart + 30), 10);

  // 数値として読めなければ無効です。
  if (isNaN(score) || isNaN(signature)) {
    return null;
  }

  return { name, score, time, signature };
}

// Unity側と同じ計算で署名を作ります。
// scoreやnameが改ざんされると、この値が一致しなくなります。
function createSignature(name, score, time) {
  let value = 173 | 0;

  for (let i = 0; i < name.length; i++) {
    value = ((value * 31) + name.charCodeAt(i)) | 0;
  }

  value = ((value * 97) + score) | 0;

  for (let i = 0; i < time.length; i++) {
    value = ((value * 17) + time.charCodeAt(i)) | 0;
  }

  for (let i = 0; i < SECRET.length; i++) {
    value = ((value * 13) + SECRET.charCodeAt(i)) | 0;
  }

  if (value < 0) {
    value = -value;
  }

  return value % 1000000000;
}

// payload内の時刻を保存用の文字列に変換します。
// 例: 202606241211 → 2026-06-24T12:11:00.000Z
function formatTimestamp(time) {
  const year = time.substring(0, 4);
  const month = time.substring(4, 6);
  const day = time.substring(6, 8);
  const hour = time.substring(8, 10);
  const minute = time.substring(10, 12);

  return `${year}-${month}-${day}T${hour}:${minute}:00.000Z`;
}

// ランキングをD1から取得します。
async function getRanking(env) {
  await createTableIfNeeded(env);

  const result =
    await env.DB.prepare(`
      SELECT
        user_name,
        score,
        created_at
      FROM scores
      ORDER BY
        score DESC,
        created_at ASC
      LIMIT 20
    `).all();

  return json({
    ok: true,
    ranking: result.results
  });
}

// D1にscoresテーブルを作成します。
// 既に存在する場合は何もしません。
async function createTableIfNeeded(env) {
  await env.DB.prepare(`
    CREATE TABLE IF NOT EXISTS scores (
      user_name TEXT PRIMARY KEY,
      score INTEGER NOT NULL,
      created_at TEXT NOT NULL
    )
  `).run();
}

// JSONを返すための共通関数です。
function json(data, status = 200) {
  return new Response(
    JSON.stringify(data),
    {
      status,
      headers: {
        "content-type":
          "application/json; charset=utf-8",
        "access-control-allow-origin":
          "*"
      }
    }
  );
}
