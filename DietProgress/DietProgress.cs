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
            WEIGHT = 6021, /* �̏d (kg) */
            BODYFATPERF = 6022, /* �̎��b��(%) */
            MUSCLEMASS = 6023, /* �ؓ���(kg) */
            MUSCLESCORE = 6024, /* �ؓ��X�R�A */
            VISCERALFATLEVEL2 = 6025, /* �������b���x��2(�����_�L��A����͊܂܂�) */
            VISCERALFATLEVEL = 6026, /* �������b���x��(�����_�����A����͊܂�) */
            BASALMETABOLISM = 6027, /* ��b��ӗ�(kcal) */
            BODYAGE = 6028, /* �̓��N��(��) */
            BONEQUANTITY = 6029 /* ���荜��(kg) */
        }

        private static HttpClientHandler handler = new HttpClientHandler()
        {
            UseCookies = true
        };
        private static HttpClient httpClient = new HttpClient();

        [FunctionName("DietProgress")]
        public static HttpResponseMessage Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequestMessage req, TraceWriter log)
        {
            /* ���ϐ�����ID,�p�X���[�h,ClientID�AClientToken���擾�A�ۊ� */
            Settings.TanitaUserID = ConfigurationManager.AppSettings.Get("TanitaUserID");
            Settings.TanitaUserPass = ConfigurationManager.AppSettings.Get("TanitaUserPass");
            Settings.TanitaClientID = ConfigurationManager.AppSettings.Get("TanitaClientID");
            Settings.TanitaClientSecretToken = ConfigurationManager.AppSettings.Get("TanitaClientSecretToken");
            Settings.DiscordWebhookUrl = ConfigurationManager.AppSettings.Get("DiscordWebhookUrl");
            Settings.OriginalWeight = Double.Parse(ConfigurationManager.AppSettings.Get("OriginalWeight"));
            Settings.GoalWeight = Double.Parse(ConfigurationManager.AppSettings.Get("GoalWeight"));

            /* �F�ؗp�f�[�^���X�N���C�s���O */
            var doc = new HtmlAgilityPack.HtmlDocument();

            httpClient.DefaultRequestHeaders.Add("Accept-Language", "ja-JP");
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/68.0.3440.106 Safari/537.36 OPR/55.0.2994.61");

            /* ���O�C������ */
            var loginProcess = Task.Run(() =>
            {
                return LoginProcess();
            });

            /* �F�؏��� */
            string htmldata = loginProcess.Result;

            doc.LoadHtml(htmldata);

            var oauthToken = doc.DocumentNode.SelectSingleNode("//input[@type='hidden' and @name='oauth_token']").Attributes["value"].Value;

            /* ���O�C������ */
            var getApprovalCode = Task.Run(() =>
            {
                return GetApprovalCode(oauthToken);
            });

            doc.LoadHtml(getApprovalCode.Result);

            var authCode = doc.DocumentNode.SelectSingleNode("//textarea[@readonly='readonly' and @id='code']").InnerText;

            /* ���N�G�X�g�g�[�N������ */
            var getAccessToken = Task.Run((Func<Task<string>>)(() =>
            {
                return (Task<string>)GetAccessToken(authCode);
            }));

            var accessToken = JsonConvert.DeserializeObject<Token>(getAccessToken.Result).access_token;

            Settings.TanitaAccessToken = accessToken;

            ConfigurationManager.AppSettings.Set("TanitaAccessToken", Settings.TanitaAccessToken);

            /* �g�̃f�[�^�擾 */
            /* ���O�C������ */
            var healthDataTask = Task.Run(() =>
            {
                return GetHealthData();
            });

            var healthData = JsonConvert.DeserializeObject<InnerScan>(healthDataTask.Result);

            var healthList = healthData.data;

            /* �ŐV�̓��t�̃f�[�^���擾 */
            healthList.Sort((a, b) => string.Compare(b.date, a.date));
            var latestDate = healthList.First().date.ToString();

            /* Discord�ɑ��邽�߂̃f�[�^��Dictionary�� */
            var latestHealthData = healthList.Where(x => x.date.Equals(latestDate)).Select(x => x).ToDictionary(x => x.tag, x => x.keydata);

            /* Discord�ɑ��M */
            var sendDiscord = Task.Run(() =>
            {
                return SendDiscord(latestHealthData, healthData.height, latestDate);
            });

            var result = sendDiscord.Result;

            return req.CreateResponse(HttpStatusCode.OK, "");
        }

        /// <summary>
        /// ���O�C���F�؏���
        /// </summary>
        /// <returns></returns>
        async static public Task<string> LoginProcess()
        {
            HttpResponseMessage response = new HttpResponseMessage();

            /* ���O�C���F�ؐ�URL */
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
        /// �g�[�N���擾�R�[�h�擾
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
        /// �A�N�Z�X�g�[�N���擾
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
        /// �g�̃f�[�^�擾
        /// </summary>
        /// <returns></returns>
        async static public Task<string> GetHealthData()
        {
            HttpResponseMessage response = new HttpResponseMessage();

            var postString = new StringBuilder();
            /* �A�N�Z�X�g�[�N�� */
            postString.Append("access_token=" + Settings.TanitaAccessToken + "&");
            /* ������t�Ŏ擾 */
            postString.Append("date=1&");
            /* �擾����From,To */
            var jst = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
            var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, jst);
            postString.Append("from=" + localTime.AddMonths(-3).ToString("yyyyMMdd") + "000000" + "&");
            postString.Append("to=" + localTime.ToString("yyyyMMdd") + "235959" + "&");
            /* �擾�f�[�^ */
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
        /// Discord���e����
        /// </summary>
        /// <param name="dic">�g�̏��</param>
        /// <param name="height">�g��</param>
        /// <param name="date">���t</param>
        /// <returns></returns>
        async static public Task<string> SendDiscord(Dictionary<String, String> dic, string height, string date)
        {
            HttpResponseMessage response = new HttpResponseMessage();

            /* Azure����UTC�ɂȂ�̂�JST�ɂ��� */
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

            /* �ڕW�B���� */
            var goal = Math.Round(((1 - (weight - Settings.GoalWeight) / (Settings.OriginalWeight - Settings.GoalWeight)) * 100), 2);

            var jsonData = new DiscordJson
            {
                content = dt.ToLongDateString() + " " + dt.ToShortTimeString() + "��Ovis�̃_�C�G�b�g�i��" + Environment.NewLine
                          + "���݂̑̏d:" + weight.ToString() + "kg" + Environment.NewLine
                          + "BMI:" + bmi.ToString() + Environment.NewLine
                          + "�ڕW�B����:" + goal.ToString() + "%" + Environment.NewLine
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