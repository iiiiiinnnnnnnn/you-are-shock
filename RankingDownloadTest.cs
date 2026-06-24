using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.StringLoading;
using VRC.SDK3.Data;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

public class RankingDownloadTest : UdonSharpBehaviour
{
    [SerializeField] private InputField nameInput;
    [SerializeField] private InputField scoreInput;
    [SerializeField] private InputField submitUrlTextInput;
    [SerializeField] private VRCUrlInputField submitUrlInput;
    [SerializeField] private Text outputText;

    private readonly VRCUrl rankingUrl = new VRCUrl("https://*****.*****.workers.dev/");
    private const string SubmitTokenBaseUrl = "https://*****.*****.workers.dev/submit-token?token=";
    private const string Secret = "ここにさっきの鍵";

    private bool isSubmitRequest;

    private string lastNameText = "";
    private string lastScoreText = "";

    private void Start()
    {
        GenerateSubmitUrl();
        LoadRanking();
    }

    private void Update()
    {
        if (nameInput == null || scoreInput == null || submitUrlTextInput == null)
        {
            return;
        }

        string currentNameText = nameInput.text;
        string currentScoreText = scoreInput.text;

        if (currentNameText == lastNameText && currentScoreText == lastScoreText)
        {
            return;
        }

        lastNameText = currentNameText;
        lastScoreText = currentScoreText;

        GenerateSubmitUrl();
    }

    public void LoadRanking()
    {
        Debug.Log("========== LoadRanking ==========");
        isSubmitRequest = false;
        VRCStringDownloader.LoadUrl(rankingUrl, (IUdonEventReceiver)this);
    }

    public void GenerateSubmitUrl()
    {
        Debug.Log("========== GenerateSubmitUrl ==========");

        if (nameInput == null || scoreInput == null || submitUrlTextInput == null)
        {
            Debug.LogError("InputField reference is missing");
            return;
        }

        string userName = nameInput.text.Trim();
        string scoreText = scoreInput.text.Trim();

        if (userName == "")
        {
            Debug.LogWarning("User name is empty");
            submitUrlTextInput.text = "";
            return;
        }

        int score;
        if (!int.TryParse(scoreText, out score))
        {
            Debug.LogWarning("Score is not a number");
            submitUrlTextInput.text = "";
            return;
        }

        if (score <= 0)
        {
            Debug.LogWarning("Score must be positive");
            submitUrlTextInput.text = "";
            return;
        }

        string time = CreateCurrentTimeText();
        string payload = CreatePayload(userName, score, time);
        string token = Encrypt(payload);
        string submitUrl = SubmitTokenBaseUrl + token;

        Debug.Log("Payload: " + payload);
        Debug.Log("Token: " + token);
        Debug.Log("SubmitUrl: " + submitUrl);

        submitUrlTextInput.text = submitUrl;
    }

    public void SubmitRanking()
    {
        Debug.Log("========== SubmitRanking ==========");

        if (submitUrlInput == null)
        {
            Debug.LogError("submitUrlInput is not set");
            return;
        }

        VRCUrl url = submitUrlInput.GetUrl();

        isSubmitRequest = true;
        VRCStringDownloader.LoadUrl(url, (IUdonEventReceiver)this);
    }

    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
        Debug.Log("========== StringLoadSuccess ==========");
        Debug.Log(result.Result);

        DataToken token;

        if (!VRCJson.TryDeserializeFromJson(result.Result, out token))
        {
            Debug.LogError("JSON parse failed");

            if (!isSubmitRequest)
            {
                outputText.text = "ランキングの取得に失敗しました";
            }

            return;
        }

        DataDictionary root = token.DataDictionary;

        if (!root.ContainsKey("ok") || !root["ok"].Boolean)
        {
            Debug.LogError("Response ok is false");

            if (!isSubmitRequest)
            {
                outputText.text = "ランキングの取得に失敗しました";
            }

            return;
        }

        if (isSubmitRequest)
        {
            Debug.Log("Submit success");

            if (root.ContainsKey("ranking"))
            {
                ShowRanking(root);
            }
            else
            {
                LoadRanking();
            }

            return;
        }

        ShowRanking(root);
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
        Debug.LogError("========== StringLoadError ==========");
        Debug.LogError(result.Error);

        if (!isSubmitRequest)
        {
            outputText.text = "ランキングの取得に失敗しました";
        }
    }

    private void ShowRanking(DataDictionary root)
    {
        if (outputText == null)
        {
            Debug.LogError("outputText is not set");
            return;
        }

        if (!root.ContainsKey("ranking"))
        {
            Debug.LogError("ranking key is missing");
            outputText.text = "ランキングの取得に失敗しました";
            return;
        }

        DataList ranking = root["ranking"].DataList;

        if (ranking.Count == 0)
        {
            outputText.text = "ランキングはまだありません";
            return;
        }

        string text = "";

        for (int i = 0; i < ranking.Count; i++)
        {
            DataDictionary row = ranking[i].DataDictionary;

            string name = row["user_name"].String;
            int score = (int)row["score"].Double;
            string time = FormatTime(row["created_at"].String);

            text += (i + 1) + "位　" + score + "スコア　" + name + "さん　(" + time + ")\n";
        }

        outputText.text = text;
    }

    private string CreateCurrentTimeText()
    {
        System.DateTime now = System.DateTime.UtcNow.AddHours(9);

        return
            PadLeftNumber(now.Year, 4) +
            PadLeftNumber(now.Month, 2) +
            PadLeftNumber(now.Day, 2) +
            PadLeftNumber(now.Hour, 2) +
            PadLeftNumber(now.Minute, 2);
    }

    private string CreatePayload(string name, int score, string time)
    {
        string nameLength = PadLeftNumber(name.Length, 3);
        string scoreText = PadLeftNumber(score, 8);

        int signature = CreateSignature(name, score, time);
        string signatureText = PadLeftNumber(signature, 10);

        return nameLength + name + scoreText + time + signatureText;
    }

    private int CreateSignature(string name, int score, string time)
    {
        int value = 173;

        for (int i = 0; i < name.Length; i++)
        {
            value = value * 31 + name[i];
        }

        value = value * 97 + score;

        for (int i = 0; i < time.Length; i++)
        {
            value = value * 17 + time[i];
        }

        for (int i = 0; i < Secret.Length; i++)
        {
            value = value * 13 + Secret[i];
        }

        if (value < 0)
        {
            value = -value;
        }

        return value % 1000000000;
    }

    private string Encrypt(string text)
    {
        string hex = "";

        for (int i = 0; i < text.Length; i++)
        {
            int encrypted = text[i] ^ Secret[i % Secret.Length];
            hex += encrypted.ToString("X2");
        }

        return hex;
    }

    private string FormatTime(string isoTime)
    {
        if (isoTime.Length < 16)
        {
            return isoTime;
        }

        int year = int.Parse(isoTime.Substring(0, 4));
        int month = int.Parse(isoTime.Substring(5, 2));
        int day = int.Parse(isoTime.Substring(8, 2));
        int hour = int.Parse(isoTime.Substring(11, 2));
        int minute = int.Parse(isoTime.Substring(14, 2));

        hour += 9;

        if (hour >= 24)
        {
            hour -= 24;
            day += 1;
        }

        return year + "年" + month + "月" + day + "日 " + hour + ":" + minute.ToString("00");
    }

    private string PadLeftNumber(int value, int length)
    {
        string text = "" + value;

        while (text.Length < length)
        {
            text = "0" + text;
        }

        if (text.Length > length)
        {
            text = text.Substring(text.Length - length, length);
        }

        return text;
    }
}