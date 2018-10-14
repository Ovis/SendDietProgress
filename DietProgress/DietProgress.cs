using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace DietProgress
{
    public static class DietProgress
    {
        enum HealthTag
        {
            WEIGHT = 6021, /* 体重 (kg) */
            BODYFATPERF = 6022, /* 体脂肪率(%) */
            MUSCLEMASS = 6023, /* 筋肉量(kg) */
            MUSCLESCORE = 6024, /* 筋肉スコア */
            VISCERALFATLEVEL2 = 6025, /* 内臓脂肪レベル2(小数点有り、手入力含まず) */
            VISCERALFATLEVEL = 6026, /* 内臓脂肪レベル(小数点無し、手入力含む) */
            BASALMETABOLISM = 6027, /* 基礎代謝量(kcal) */
            BODYAGE = 6028, /* 体内年齢(歳) */
            BONEQUANTITY = 6029 /* 推定骨量(kg) */
        }

        private static HttpClientHandler handler = new HttpClientHandler()
        {
            UseCookies = true
        };
        private static HttpClient httpClient = new HttpClient();

        [FunctionName("DietProgress")]
        public static HttpResponseMessage Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequestMessage req, TraceWriter log)
        {
            /* 環境変数からID,パスワード,ClientID、ClientTokenを取得、保管 */
            Settings.TanitaUserID = ConfigurationManager.AppSettings.Get("TanitaUserID");
            Settings.TanitaUserPass = ConfigurationManager.AppSettings.Get("TanitaUserPass");
            Settings.TanitaClientID = ConfigurationManager.AppSettings.Get("TanitaClientID");
            Settings.TanitaClientSecretToken = ConfigurationManager.AppSettings.Get("TanitaClientSecretToken");
            Settings.DiscordWebhookUrl = ConfigurationManager.AppSettings.Get("DiscordWebhookUrl");
            Settings.OriginalWeight = Double.Parse(ConfigurationManager.AppSettings.Get("OriginalWeight"));
            Settings.GoalWeight = Double.Parse(ConfigurationManager.AppSettings.Get("GoalWeight"));

            /* 認証用データをスクレイピング */
            var doc = new HtmlAgilityPack.HtmlDocument();

            httpClient.DefaultRequestHeaders.Add("Accept-Language", "ja-JP");
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/68.0.3440.106 Safari/537.36 OPR/55.0.2994.61");

            /* ログイン処理 */
            var loginProcess = Task.Run(() =>
            {
                return LoginProcess();
            });

            /* 認証処理 */
            string htmldata = loginProcess.Result;

            doc.LoadHtml(htmldata);

            var oauthToken = doc.DocumentNode.SelectSingleNode("//input[@type='hidden' and @name='oauth_token']").Attributes["value"].Value;

            /* ログイン処理 */
            var getApprovalCode = Task.Run(() =>
            {
                return GetApprovalCode(oauthToken);
            });

            doc.LoadHtml(getApprovalCode.Result);

            var authCode = doc.DocumentNode.SelectSingleNode("//textarea[@readonly='readonly' and @id='code']").InnerText;

            /* リクエストトークン処理 */
            var getAccessToken = Task.Run((Func<Task<string>>)(() =>
            {
                return (Task<string>)GetAccessToken(authCode);
            }));

            var accessToken = JsonConvert.DeserializeObject<Token>(getAccessToken.Result).access_token;

            Settings.TanitaAccessToken = accessToken;

            ConfigurationManager.AppSettings.Set("TanitaAccessToken", Settings.TanitaAccessToken);

            /* 身体データ取得 */
            /* ログイン処理 */
            var healthDataTask = Task.Run(() =>
            {
                return GetHealthData();
            });

            var healthData = JsonConvert.DeserializeObject<InnerScan>(healthDataTask.Result);

            var healthList = healthData.data;

            /* 最新の日付のデータを取得 */
            healthList.Sort((a, b) => string.Compare(b.date, a.date));
            var latestDate = healthList.First().date.ToString();

            /* Discordに送るためのデータをDictionary化 */
            var latestHealthData = healthList.Where(x => x.date.Equals(latestDate)).Select(x => x).ToDictionary(x => x.tag, x => x.keydata);

            /* Discordに送信 */
            var sendDiscord = Task.Run(() =>
            {
                return SendDiscord(latestHealthData, healthData.height, latestDate);
            });

            var result = sendDiscord.Result;

            return req.CreateResponse(HttpStatusCode.OK, "");
        }

        /// <summary>
        /// ログイン認証処理
        /// </summary>
        /// <returns></returns>
        async static public Task<string> LoginProcess()
        {
            HttpResponseMessage response = new HttpResponseMessage();

            /* ログイン認証先URL */
            var authUrl = new StringBuilder();
            authUrl.Append("https://www.healthplanet.jp/oauth/auth?");
            authUrl.Append("client_id=" + Settings.TanitaClientID);
            authUrl.Append("&redirect_uri=https://localhost/");
            authUrl.Append("&scope=innerscan");
            authUrl.Append("&response_type=code");

            var postString = new StringBuilder();
            postString.Append("loginId=" + Settings.TanitaUserID + "&");
            postString.Append("passwd=" + Settings.TanitaUserPass + "&");
            postString.Append("send=1&");
            postString.Append("url=" + HttpUtility.UrlEncode(authUrl.ToString(), Encoding.GetEncoding("shift_jis")));

            StringContent contentShift = new StringContent(postString.ToString(), Encoding.GetEncoding("shift_jis"), "application/x-www-form-urlencoded");

            response = await httpClient.PostAsync("https://www.healthplanet.jp/login_oauth.do", contentShift);

            CookieCollection cookies = handler.CookieContainer.GetCookies(new Uri("https://www.healthplanet.jp/"));

            using (Stream stream = (await response.Content.ReadAsStreamAsync()))
            using (TextReader reader = (new StreamReader(stream, Encoding.GetEncoding("Shift_JIS"), true)) as TextReader)
            {
                return await reader.ReadToEndAsync();
            }
        }

        /// <summary>
        /// トークン取得コード取得
        /// </summary>
        /// <param name="oAuthToken"></param>
        /// <returns></returns>
        async static public Task<string> GetApprovalCode(String oAuthToken)
        {
            HttpResponseMessage response = new HttpResponseMessage();

            var postString = new StringBuilder();
            postString.Append("approval=true&");
            postString.Append("oauth_token=" + oAuthToken + "&");

            StringContent contentShift = new StringContent(postString.ToString(), Encoding.GetEncoding("shift_jis"), "application/x-www-form-urlencoded");

            response = await httpClient.PostAsync("https://www.healthplanet.jp/oauth/approval.do", contentShift);

            using (Stream stream = (await response.Content.ReadAsStreamAsync()))
            using (TextReader reader = (new StreamReader(stream, Encoding.GetEncoding("Shift_JIS"), true)) as TextReader)
            {
                return await reader.ReadToEndAsync();
            }
        }

        /// <summary>
        /// アクセストークン取得
        /// </summary>
        /// <param name="oAuthToken"></param>
        /// <returns></returns>
        async static public Task<string> GetAccessToken(String oAuthToken)
        {
            HttpResponseMessage response = new HttpResponseMessage();

            var postString = new StringBuilder();
            postString.Append("client_id=" + Settings.TanitaClientID + "&");
            postString.Append("client_secret=" + Settings.TanitaClientSecretToken + "&");
            postString.Append("redirect_uri=" + HttpUtility.UrlEncode("http://localhost/", Encoding.GetEncoding("shift_jis")) + "&");
            postString.Append("code=" + oAuthToken + "&");
            postString.Append("grant_type=authorization_code");

            StringContent contentShift = new StringContent(postString.ToString(), Encoding.GetEncoding("shift_jis"), "application/x-www-form-urlencoded");

            response = await httpClient.PostAsync("https://www.healthplanet.jp/oauth/token", contentShift);

            using (Stream stream = (await response.Content.ReadAsStreamAsync()))
            using (TextReader reader = (new StreamReader(stream, Encoding.GetEncoding("Shift_JIS"), true)) as TextReader)
            {
                return await reader.ReadToEndAsync();
            }
        }

        /// <summary>
        /// 身体データ取得
        /// </summary>
        /// <returns></returns>
        async static public Task<string> GetHealthData()
        {
            HttpResponseMessage response = new HttpResponseMessage();

            var postString = new StringBuilder();
            /* アクセストークン */
            postString.Append("access_token=" + Settings.TanitaAccessToken + "&");
            /* 測定日付で取得 */
            postString.Append("date=1&");
            /* 取得期間From,To */
            var jst = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
            var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, jst);
            postString.Append("from=" + localTime.AddMonths(-3).ToString("yyyyMMdd") + "000000" + "&");
            postString.Append("to=" + localTime.ToString("yyyyMMdd") + "235959" + "&");
            /* 取得データ */
            postString.Append("tag=6021,6022,6023,6024,6025,6026,6027,6028,6029" + "&");

            StringContent contentShift = new StringContent(postString.ToString(), Encoding.UTF8, "application/x-www-form-urlencoded");

            response = await httpClient.PostAsync("https://www.healthplanet.jp/status/innerscan.json", contentShift);

            using (Stream stream = (await response.Content.ReadAsStreamAsync()))
            using (TextReader reader = (new StreamReader(stream, Encoding.UTF8, true)) as TextReader)
            {
                return await reader.ReadToEndAsync();
            }
        }

        /// <summary>
        /// Discord投稿処理
        /// </summary>
        /// <param name="dic">身体情報</param>
        /// <param name="height">身長</param>
        /// <param name="date">日付</param>
        /// <returns></returns>
        async static public Task<string> SendDiscord(Dictionary<String, String> dic, string height, string date)
        {
            HttpResponseMessage response = new HttpResponseMessage();

            /* AzureだとUTCになるのでJSTにする */
            var jst = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
            var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, jst);

            DateTime dt = new DateTime();

            if (!DateTime.TryParseExact(date, "yyyyMMddHHmm", null, DateTimeStyles.AssumeLocal, out dt))
            {
                dt = localTime;
            }

            /* BMI */
            var cm = double.Parse(height) / 100;
            var weight = double.Parse(dic[((int)HealthTag.WEIGHT).ToString()].ToString());
            var bmi = Math.Round((weight / Math.Pow(cm, 2)), 2);

            /* 目標達成率 */
            var goal = Math.Round(((1 - (weight - Settings.GoalWeight) / (Settings.OriginalWeight - Settings.GoalWeight)) * 100), 2);

            var jsonData = new DiscordJson
            {
                content = dt.ToLongDateString() + " " + dt.ToShortTimeString() + "のOvisのダイエット進捗" + Environment.NewLine
                          + "現在の体重:" + weight.ToString() + "kg" + Environment.NewLine
                          + "BMI:" + bmi.ToString() + Environment.NewLine
                          + "目標達成率:" + goal.ToString() + "%" + Environment.NewLine
            };

            var json = JsonConvert.SerializeObject(jsonData);

            StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

            response = await httpClient.PostAsync(Settings.DiscordWebhookUrl, content);

            using (Stream stream = (await response.Content.ReadAsStreamAsync()))
            using (TextReader reader = (new StreamReader(stream, Encoding.UTF8, true)) as TextReader)
            {
                return await reader.ReadToEndAsync();
            }
        }
    }
}