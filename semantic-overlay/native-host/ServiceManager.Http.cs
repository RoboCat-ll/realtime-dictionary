using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SemanticOverlay.NativeHost
{
    internal sealed partial class ServiceManager : IDisposable
    {
        private T PostJson<T>(string url, object payload)
        {
            return PostJson<T>(url, payload, 3000);
        }

        private T PostJson<T>(string url, object payload, int timeout)
        {
            if (url.EndsWith("/caption/translate", StringComparison.Ordinal)) EnsureCloudConsent(true);
            if (url.EndsWith("/analyze", StringComparison.Ordinal) && !url.EndsWith("/selection/analyze", StringComparison.Ordinal))
                EnsureCloudConsent(false);
            byte[] body = Encoding.UTF8.GetBytes(serializer.Serialize(payload));
            for (int attempt = 0; ; attempt++)
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Timeout = timeout;
                request.ReadWriteTimeout = timeout;
                SetToken(request);
                request.ContentLength = body.Length;
                try
                {
                    using (Stream requestStream = request.GetRequestStream())
                        requestStream.Write(body, 0, body.Length);
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        return serializer.Deserialize<T>(reader.ReadToEnd());
                }
                catch (WebException error)
                {
                    if (attempt == 0 && IsForbidden(error))
                    {
                        ResetServiceToken();
                        continue;
                    }
                    throw;
                }
            }
        }

        private T GetJson<T>(string url)
        {
            return GetJson<T>(url, true);
        }

        private T GetJson<T>(string url, bool withToken)
        {
            for (int attempt = 0; ; attempt++)
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 1000;
                if (withToken)
                    SetToken(request);
                try
                {
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        return serializer.Deserialize<T>(reader.ReadToEnd());
                }
                catch (WebException error)
                {
                    if (attempt == 0 && withToken && IsForbidden(error))
                    {
                        ResetServiceToken();
                        continue;
                    }
                    throw;
                }
            }
        }

        private string GetServiceToken()
        {
            lock (tokenLock)
            {
                if (String.IsNullOrEmpty(serviceToken))
                {
                    try
                    {
                        SessionInfo session = GetJson<SessionInfo>("http://127.0.0.1:8877/session", false);
                        if (session != null && !String.IsNullOrEmpty(session.token) &&
                            String.Equals(session.product_id, ExpectedProductId, StringComparison.Ordinal) &&
                            session.protocol_version == SupportedProtocolVersion)
                            serviceToken = session.token;
                        else if (session != null)
                            throw new InvalidOperationException("后端会话协议与当前程序不兼容。");
                    }
                    catch (Exception error)
                    {
                        Log("Fetch service token failed: " + error.GetType().Name);
                    }
                }
                return serviceToken;
            }
        }

        private void ResetServiceToken()
        {
            lock (tokenLock)
            {
                serviceToken = null;
            }
        }

        private void SetToken(HttpWebRequest request)
        {
            string token = GetServiceToken();
            if (!String.IsNullOrEmpty(token))
                request.Headers["X-RealtimeDictionary-Token"] = token;
        }

        private static bool IsForbidden(WebException error)
        {
            HttpWebResponse response = error.Response as HttpWebResponse;
            return response != null && response.StatusCode == HttpStatusCode.Forbidden;
        }

    }
}
