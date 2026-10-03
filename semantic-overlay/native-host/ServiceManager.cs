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
    internal static class CredentialProtection
    {
        internal static string EndpointIdentity(string endpoint)
        {
            Uri uri = new Uri(endpoint.Trim().TrimEnd('/'));
            return uri.Scheme.ToLowerInvariant() + "://" + uri.Host.ToLowerInvariant() + ":" +
                uri.Port + uri.AbsolutePath.TrimEnd('/');
        }

        internal static void Save(string path, Dictionary<string, object> changes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            Dictionary<string, object> config = new Dictionary<string, object>();
            if (File.Exists(path))
            {
                Dictionary<string, object> existing = serializer.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(path, Encoding.UTF8));
                if (existing != null)
                    config = existing;
            }
            foreach (KeyValuePair<string, object> change in changes)
            {
                if (change.Key == "api_key" || change.Key == "text_api_key")
                {
                    string endpointField = change.Key == "api_key" ? "base_url" : "text_base_url";
                    string endpoint = Convert.ToString(changes[endpointField]);
                    config[change.Key + "_protected"] = CredentialProtection.Protect(
                        Convert.ToString(change.Value), endpoint, change.Key);
                    config.Remove(change.Key);
                }
                else config[change.Key] = change.Value;
            }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(temporary, serializer.Serialize(config), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static string Protect(string key, string endpoint, string slot)
        {
            byte[] entropy = Encoding.UTF8.GetBytes("RealtimeDictionary|v1|" + slot + "|" + EndpointIdentity(endpoint));
            byte[] bytes = Encoding.UTF8.GetBytes(key);
            try { return "dpapi-v1:" + Convert.ToBase64String(ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
    }

    internal sealed class ProviderUsageSummary
    {
        public string date { get; set; }
        public string billing_notice { get; set; }
        public List<ProviderUsageGroup> groups { get; set; }
    }
    internal sealed class ProviderUsageGroup
    {
        public string provider { get; set; }
        public string model { get; set; }
        public int requests { get; set; }
        public int unknown_usage_requests { get; set; }
        public Dictionary<string, long> reported_tokens { get; set; }
    }

    internal sealed partial class ServiceManager : IDisposable
    {
        internal const string ExpectedProductId = "realtime-dictionary";
        internal const int SupportedProtocolVersion = 2;
        internal static readonly string ClientVersion = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "version.txt"))).Trim();
        internal static string WorkModeOverrideForDiagnostics { get; set; }
        internal static bool DisableFamiliarityPersistenceForDiagnostics { get; set; }
        internal static bool DisableUsageMetricsForDiagnostics { get; set; }

        private readonly string projectRoot;
        private readonly string logPath;
        private readonly List<Process> ownedProcesses = new List<Process>();
        private readonly object serviceLock = new object();
        private readonly object windowsOcrLock = new object();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private readonly object tokenLock = new object();
        private readonly PreferenceStore preferences;
        private readonly HashSet<string> ignoredTerms =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly TermFamiliarityStore familiarity;
        private readonly UsageMetricsStore usageMetrics;
        private string serviceToken;
        private Process windowsOcrWorker;
        private Task<string> windowsOcrErrorTask;

        public string Difficulty { get; private set; }
        public string AnalysisContextId { get; private set; }
        public string ScanScope { get; private set; }
        public string WorkMode { get; private set; }
        public string PresentationMode { get; private set; }
        public string CaptionAudioScope { get; private set; }
        public bool AutoLearnFamiliarTerms { get; private set; }
        public bool SelectionToolbarEnabled { get; private set; }
        public bool ExperimentalFeaturesEnabled { get; private set; }
        public bool CaptionPromptEnabled { get; private set; }
        public bool CaptionArchiveEnabled { get; private set; }
        public bool ContinuousLookupEnabled { get; private set; }
        public string ContinuousLookupTrigger { get; private set; }

        // 诊断隔离钩子（与 WorkModeOverrideForDiagnostics 同模式）：
        // .NET GetFolderPath 不读 APPDATA 环境变量，偏好隔离必须显式指定路径。
        internal static string PreferencePathOverrideForDiagnostics;
        internal static bool DisableFloatSizeRestoreForDiagnostics;
        public bool CaptionDisclosureAccepted { get; private set; }
        public Func<string, bool> CloudConsentRequested;
        private readonly object cloudConsentLock = new object();
        public int IgnoredTermCount { get { return ignoredTerms.Count; } }
        public int AutoSuppressedTermCount { get { return familiarity.SuppressedCount; } }

        public void OpenUsageMetricsDirectory()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RealtimeDictionary");
            Directory.CreateDirectory(directory);
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = "explorer.exe";
            info.Arguments = "\"" + directory + "\"";
            info.UseShellExecute = true;
            Process.Start(info);
        }

        public ServiceManager()
        {
            projectRoot = FindProjectRoot();
            logPath = Path.Combine(projectRoot, "_native_host.log");
            preferences = new PreferenceStore(PreferencePathOverrideForDiagnostics ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RealtimeDictionary", "preferences.json"), delegate(Exception error) {
                Log("Load preferences failed: " + error.GetType().Name);
            });
            string value;
            Difficulty = preferences.TryGetValue("difficulty", out value) &&
                (value == "concise" || value == "standard" || value == "detailed")
                ? value : "standard";
            ScanScope = preferences.TryGetValue("scan_scope", out value) && value == "full"
                ? "full" : "auto";
            if (WorkModeOverrideForDiagnostics == "caption" ||
                WorkModeOverrideForDiagnostics == "conversation")
                WorkMode = WorkModeOverrideForDiagnostics;
            else
                WorkMode = preferences.TryGetValue("work_mode", out value) && value == "caption"
                    ? "caption" : "conversation";
            CaptionAudioScope = preferences.TryGetValue("caption_audio_scope", out value) && value == "system"
                ? "system" : "process";
            PresentationMode = preferences.TryGetValue("presentation_mode", out value) && value == "attached"
                ? "attached" : "assistant";
            AutoLearnFamiliarTerms = !preferences.TryGetValue("auto_learn_familiar_terms", out value) ||
                !String.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            LoadIgnoredTerms();
            SelectionToolbarEnabled = !preferences.TryGetValue("selection_toolbar", out value) || value != "false";
            ExperimentalFeaturesEnabled = preferences.TryGetValue("experimental_features", out value) &&
                String.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            CaptionPromptEnabled = !preferences.TryGetValue("caption_prompt_enabled", out value) ||
                !String.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            CaptionArchiveEnabled = !preferences.TryGetValue("caption_archive_enabled", out value) || value != "false";
            CaptionDisclosureAccepted = preferences.TryGetValue("caption_disclosure_v2", out value) && value == "true";
            ContinuousLookupEnabled = preferences.TryGetValue("continuous_lookup", out value) &&
                String.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            ContinuousLookupTrigger = preferences.TryGetValue("continuous_lookup_trigger", out value) &&
                value == "alt_click" ? "alt_click" : "double_click";
            if (!ExperimentalFeaturesEnabled && WorkMode == "caption" &&
                WorkModeOverrideForDiagnostics != "caption")
                WorkMode = "conversation";
            familiarity = new TermFamiliarityStore(
                DisableFamiliarityPersistenceForDiagnostics ? null : FamiliarityPath());
            usageMetrics = DisableUsageMetricsForDiagnostics ? null : new UsageMetricsStore();
            BeginAnalysisContext();
            Task.Factory.StartNew(delegate
            {
                try
                {
                    lock (windowsOcrLock)
                        EnsureWindowsOcrWorker();
                }
                catch (Exception error)
                {
                    Log("Windows OCR worker warmup failed: " + error.GetType().Name);
                }
            });
        }

        public void BeginAnalysisContext()
        {
            familiarity.EndSession(AutoLearnFamiliarTerms);
            AnalysisContextId = Guid.NewGuid().ToString("N");
            familiarity.BeginSession();
        }

        public void EndAnalysisContext()
        {
            familiarity.EndSession(AutoLearnFamiliarTerms);
        }

        public void UpdateVisibleTerms(IEnumerable<string> terms)
        {
            familiarity.UpdateVisibleTerms(terms);
        }

        public void NoteTermClicked(string term)
        {
            familiarity.NoteClicked(term);
        }

        public void RecordLookupMetric(string triggerMode, string sourceApp, string textSource, string lookupMode,
            long elapsedMilliseconds, bool manualCorrection, bool cacheHit, bool success)
        {
            if (usageMetrics != null)
                usageMetrics.RecordLookup(triggerMode, sourceApp, textSource, lookupMode,
                    elapsedMilliseconds > Int32.MaxValue ? Int32.MaxValue : (int)elapsedMilliseconds,
                manualCorrection, cacheHit, success);
        }

        public void RecordSelectionMetric(string sourceApp, string textSource, string analysisMode,
            int elapsedMs, bool manualCorrection, bool success, int termCount)
        {
            if (usageMetrics != null)
                usageMetrics.RecordSelection(sourceApp, textSource, analysisMode,
                    elapsedMs, manualCorrection, success, termCount);
        }

        public void RecordFeedbackMetric(string triggerMode, string sourceApp, string feedback)
        {
            if (usageMetrics != null) usageMetrics.RecordFeedback(triggerMode, sourceApp, feedback);
        }

        public void RecordHighlightMetric(string sourceApp, int count, string analysisMode)
        {
            if (usageMetrics != null) usageMetrics.RecordHighlights(sourceApp, count, analysisMode);
        }

        public bool ShouldSuppressTerm(string term)
        {
            return AutoLearnFamiliarTerms && familiarity.ShouldSuppress(term);
        }

        public void SetAutoLearnFamiliarTerms(bool enabled)
        {
            if (AutoLearnFamiliarTerms == enabled) return;
            if (!enabled) familiarity.EndSession(false);
            AutoLearnFamiliarTerms = enabled;
            preferences.SetAndSave("auto_learn_familiar_terms", enabled ? "true" : "false");
            if (enabled) familiarity.BeginSession();
            Log("Local familiarity learning " + (enabled ? "enabled" : "disabled"));
        }

        public void SetExperimentalFeaturesEnabled(bool enabled)
        {
            ExperimentalFeaturesEnabled = enabled;
            preferences.SetAndSave("experimental_features", enabled ? "true" : "false");
            Log("Experimental features " + (enabled ? "enabled" : "disabled"));
        }

        public void SetCaptionPromptEnabled(bool enabled)
        {
            CaptionPromptEnabled = enabled;
            preferences.SetAndSave("caption_prompt_enabled", enabled ? "true" : "false");
            Log("Caption startup prompt " + (enabled ? "enabled" : "disabled"));
        }

        public void SetCaptionArchiveEnabled(bool enabled)
        {
            CaptionArchiveEnabled = enabled;
            preferences.SetAndSave("caption_archive_enabled", enabled ? "true" : "false");
        }

        public void SetContinuousLookupEnabled(bool enabled)
        {
            ContinuousLookupEnabled = enabled;
            preferences.SetAndSave("continuous_lookup", enabled ? "true" : "false");
            Log("Continuous lookup mode " + (enabled ? "enabled" : "disabled"));
        }

        public void SetContinuousLookupTrigger(string value)
        {
            ContinuousLookupTrigger = value == "alt_click" ? "alt_click" : "double_click";
            preferences.SetAndSave("continuous_lookup_trigger", ContinuousLookupTrigger);
            Log("Continuous lookup trigger changed to " + ContinuousLookupTrigger);
        }

        // 浮框尺寸以 96DPI 逻辑像素持久化（"WxH"），恢复时按当前 DPI 缩放。
        private Size sessionFloatSize = Size.Empty;

        public bool TryGetFloatSize(out Size logicalSize)
        {
            if (!sessionFloatSize.IsEmpty) { logicalSize = sessionFloatSize; return true; }
            logicalSize = Size.Empty;
            string value;
            if (!preferences.TryGetValue("float_size", out value) || String.IsNullOrWhiteSpace(value))
                return false;
            string[] parts = value.Split('x', 'X');
            int width, height;
            if (parts.Length != 2 || !Int32.TryParse(parts[0], out width) ||
                !Int32.TryParse(parts[1], out height)) return false;
            if (width < 320 || width > 16384 || height < 160 || height > 16384) return false;
            logicalSize = new Size(width, height);
            return true;
        }

        public void SetFloatSize(Size logicalSize)
        {
            preferences.SetAndSave("float_size", logicalSize.Width + "x" + logicalSize.Height);
        }

        public bool TrySetFloatSize(Size logicalSize)
        {
            sessionFloatSize = logicalSize;
            try { SetFloatSize(logicalSize); return true; }
            catch (UnauthorizedAccessException) { Log("Float size save denied; current-session size retained."); }
            catch (IOException) { Log("Float size save unavailable; current-session size retained."); }
            return false;
        }

        public void AcceptCaptionDisclosure()
        {
            CaptionDisclosureAccepted = true;
            preferences.SetAndSave("caption_disclosure_v2", "true");
        }

        private void EnsureCloudConsent(bool textProvider)
        {
            // No configured model means local-only processing and no upload prompt.
            ServiceHealth health = GetServiceStatus();
            string endpoint = textProvider ? health.text_base_url : health.base_url;
            if (textProvider ? !health.has_explanation_key : !health.has_key) return;
            Uri provider;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out provider))
                throw new InvalidOperationException("无法确认文字服务商，请检查配置。");
            string key = "cloud_text_approved:" + endpoint;
            lock (cloudConsentLock)
            {
                string value;
                if (preferences.TryGetValue(key, out value) && value == "true") return;
                if (CloudConsentRequested == null || !CloudConsentRequested(provider.Host))
                    throw new InvalidOperationException("尚未同意发送文字，本次请求未发送。");
                preferences.SetAndSave(key, "true");
            }
        }

        public void ClearFamiliarTerms()
        {
            familiarity.Clear();
            Log("Cleared local familiarity counters");
        }

        public void SetDifficulty(string value)
        {
            if (value != "concise" && value != "standard" && value != "detailed")
                value = "standard";
            Difficulty = value;
            preferences.SetAndSave("difficulty", value);
            Log("Highlight difficulty changed to " + value);
        }

        public void SetSelectionToolbarEnabled(bool enabled)
        {
            SelectionToolbarEnabled = enabled;
            preferences.SetAndSave("selection_toolbar", enabled ? "true" : "false");
        }

        public void SetScanScope(string value)
        {
            ScanScope = value == "full" ? "full" : "auto";
            preferences.SetAndSave("scan_scope", ScanScope);
            Log("Scan scope changed to " + ScanScope);
        }

        public void SetWorkMode(string value)
        {
            WorkMode = value == "caption" ? "caption" : "conversation";
            preferences.SetAndSave("work_mode", WorkMode);
            Log("Work mode changed to " + WorkMode);
        }

        public void SetPresentationMode(string value)
        {
            PresentationMode = value == "attached" ? "attached" : "assistant";
            preferences.SetAndSave("presentation_mode", PresentationMode);
            Log("Conversation presentation changed to " + PresentationMode);
        }

        public void SetCaptionAudioScope(string value)
        {
            CaptionAudioScope = value == "system" ? "system" : "process";
            preferences.SetAndSave("caption_audio_scope", CaptionAudioScope);
            Log("Caption audio scope changed to " + CaptionAudioScope);
        }

        private string IgnoredTermsPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RealtimeDictionary",
                "ignored-terms.txt");
        }

        private string FamiliarityPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RealtimeDictionary",
                "term-familiarity.json");
        }

        private void LoadIgnoredTerms()
        {
            try
            {
                string path = IgnoredTermsPath();
                if (!File.Exists(path))
                    return;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string term = line.Trim();
                    if (term.Length > 0)
                        ignoredTerms.Add(term);
                }
            }
            catch (Exception error)
            {
                Log("Load ignored terms failed: " + error.GetType().Name);
            }
        }

        public bool IsIgnoredTerm(string term)
        {
            return !String.IsNullOrWhiteSpace(term) && ignoredTerms.Contains(term.Trim());
        }

        public void IgnoreTerm(string term)
        {
            string value = (term ?? String.Empty).Trim();
            if (value.Length == 0 || !ignoredTerms.Add(value))
                return;
            string path = IgnoredTermsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, ignoredTerms, new UTF8Encoding(false));
        }

        public void ClearIgnoredTerms()
        {
            ignoredTerms.Clear();
            string path = IgnoredTermsPath();
            if (File.Exists(path))
                File.WriteAllText(path, String.Empty, new UTF8Encoding(false));
        }

        public void EnsureRunning()
        {
            lock (serviceLock)
            {
                ServiceHealth health = TryGetAnalysisHealth(350);
                if (health != null)
                {
                    if (IsCompatibleHealth(health))
                        return;
                    throw new InvalidOperationException(
                        "端口 8877 正在运行旧版或不兼容的实时字典服务。请先从托盘退出旧版本，再启动当前版本。");
                }
                if (IsHealthy("http://127.0.0.1:8877/health", 350))
                    throw new InvalidOperationException("端口 8877 已被其他本地服务占用。");
                StartPython("server.py");
                WaitForCompatibleAnalysisService(10000);
            }
        }

        public ScanResponse RefineWords(List<OcrWord> words)
        {
            return AnalyzeWords(words ?? new List<OcrWord>(), "model");
        }

        public void SaveApiKey(string apiKey, string baseUrl, string model)
        {
            string key;
            string endpoint;
            string modelId;
            NormalizeProviderConfig(apiKey, baseUrl, model, out key, out endpoint, out modelId);
            Dictionary<string, object> changes = new Dictionary<string, object>();
            changes["base_url"] = endpoint;
            changes["model"] = modelId;
            changes["api_key"] = key;
            UpdateProviderConfig(changes);
        }

        public void SaveTextApiKey(string apiKey, string baseUrl, string model)
        {
            string key, endpoint, modelId;
            NormalizeProviderConfig(apiKey, baseUrl, model, out key, out endpoint, out modelId);
            var changes = new Dictionary<string, object>();
            changes["text_base_url"] = endpoint;
            changes["text_model"] = modelId;
            changes["text_api_key"] = key;
            UpdateProviderConfig(changes);
        }



        private static void NormalizeProviderConfig(
            string apiKey, string baseUrl, string model,
            out string key, out string endpoint, out string modelId)
        {
            key = (apiKey ?? string.Empty).Trim();
            if (key.Length < 8)
                throw new ArgumentException("API Key 不能为空。", "apiKey");
            endpoint = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            modelId = (model ?? string.Empty).Trim();
            Uri endpointUri;
            bool validUri = Uri.TryCreate(endpoint, UriKind.Absolute, out endpointUri);
            bool localHttp = validUri && endpointUri.Scheme == Uri.UriSchemeHttp &&
                (endpointUri.Host == "127.0.0.1" || endpointUri.Host == "localhost");
            if (!validUri || string.IsNullOrWhiteSpace(endpointUri.Host) ||
                endpointUri.UserInfo.Length != 0 || endpointUri.Query.Length != 0 ||
                endpointUri.Fragment.Length != 0 ||
                (endpointUri.Scheme != Uri.UriSchemeHttps && !localHttp))
                throw new ArgumentException("模型服务地址必须是 HTTPS，或本机地址。", "baseUrl");
            if (modelId.Length == 0 || modelId.Length > 160)
                throw new ArgumentException("模型名称不能为空。", "model");
        }

        private void UpdateProviderConfig(Dictionary<string, object> changes)
        {
            string configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RealtimeDictionary");
            Directory.CreateDirectory(configDir);
            string path = Path.Combine(configDir, "config.json");
            CredentialProtection.Save(path, changes);
        }

        public bool TakeFirstRunNotice()
        {
            try
            {
                string stateDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RealtimeDictionary");
                string marker = Path.Combine(stateDir, "onboarding-v1.done");
                if (File.Exists(marker))
                    return false;
                Directory.CreateDirectory(stateDir);
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"), new UTF8Encoding(false));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public KeyValidationResult ValidateApiKey(string apiKey, string baseUrl, string model)
        {
            Dictionary<string, string> payload = new Dictionary<string, string>();
            payload["api_key"] = (apiKey ?? string.Empty).Trim();
            payload["base_url"] = (baseUrl ?? string.Empty).Trim();
            payload["model"] = (model ?? string.Empty).Trim();
            return PostJson<KeyValidationResult>(
                "http://127.0.0.1:8877/validate-key", payload, 12000);
        }



        public KeyValidationResult ValidateCurrentConfiguration()
        {
            return PostJson<KeyValidationResult>(
                "http://127.0.0.1:8877/validate-current",
                new Dictionary<string, string>(),
                12000);
        }

        public ServiceHealth GetServiceStatus()
        {
            return GetJson<ServiceHealth>("http://127.0.0.1:8877/health", false);
        }

        public string GetProviderUsage()
        {
            EnsureRunning();
            ProviderUsageSummary usage = GetJson<ProviderUsageSummary>("http://127.0.0.1:8877/usage");
            StringBuilder text = new StringBuilder("日期：" + usage.date + "\r\n" + usage.billing_notice + "\r\n");
            foreach (ProviderUsageGroup group in usage.groups ?? new List<ProviderUsageGroup>()) {
                text.Append("\r\n" + group.provider + " / " + group.model + "\r\n请求：" + group.requests +
                    " 次；用量未知：" + group.unknown_usage_requests + " 次\r\n");
                long input, output;
                if (group.reported_tokens.TryGetValue("prompt_tokens", out input)) text.Append("已报告输入：" + input + " tokens\r\n");
                if (group.reported_tokens.TryGetValue("completion_tokens", out output)) text.Append("已报告输出：" + output + " tokens\r\n");
            }
            return text.ToString();
        }

        internal static bool IsCompatibleHealth(ServiceHealth health)
        {
            return health != null && health.ok &&
                String.Equals(health.product_id, ExpectedProductId, StringComparison.Ordinal) &&
                health.protocol_version == SupportedProtocolVersion && health.app_version == ClientVersion;
        }

        public KeyValidationResult RestartAnalysisService()
        {
            ServiceHealth current = TryGetAnalysisHealth(500);
            if (current != null && !IsCompatibleHealth(current))
                throw new InvalidOperationException(
                    "检测到旧版或不兼容的后端。请先退出旧版实时字典，再重新打开当前版本。");
            if (current == null && IsHealthy("http://127.0.0.1:8877/health", 350))
                throw new InvalidOperationException("端口 8877 已被其他本地服务占用，不能自动重启。");
            try
            {
                PostJson<OperationResponse>(
                    "http://127.0.0.1:8877/shutdown",
                    new Dictionary<string, string>(),
                    3000);
            }
            catch (Exception error)
            {
                Log("Graceful server shutdown request failed: " + error.GetType().Name);
            }
            ResetServiceToken();
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 4000 &&
                   IsHealthy("http://127.0.0.1:8877/health", 250))
                Thread.Sleep(100);
            lock (serviceLock)
            {
                if (IsHealthy("http://127.0.0.1:8877/health", 250))
                    throw new InvalidOperationException("旧分析服务没有按时退出。");
                StartPython("server.py");
                WaitForCompatibleAnalysisService(10000);
            }
            ResetServiceToken();
            ServiceHealth health = GetServiceStatus();
            bool configured = health != null && health.has_explanation_key;
            return new KeyValidationResult {
                ok = health != null && health.ok && configured,
                configured = configured,
                model = health == null ? null : health.explanation_model,
                model_available = health != null && health.ok && configured,
                message = health != null && health.ok && configured
                    ? "配置已应用" : "配置未能加载"
            };
        }

        public LookupResponse Lookup(
            string term,
            string context,
            bool refresh,
            string previousExplanation,
            string detail = "full")
        {
            return LookupCore(term, context, refresh, previousExplanation, false, detail);
        }

        public LookupResponse LookupInstant(string term, string context)
        {
            return LookupCore(term, context, false, null, true);
        }

        public SelectionAnalysisResponse AnalyzeSelection(string text, bool allowOcrCorrection,
            bool refresh)
        {
            EnsureCloudConsent(true);
            Dictionary<string, string> payload = new Dictionary<string, string>();
            payload["text"] = text;
            payload["allow_ocr_correction"] = allowOcrCorrection ? "true" : "false";
            payload["refresh"] = refresh ? "true" : "false";
            SelectionAnalysisResponse result = PostJson<SelectionAnalysisResponse>(
                "http://127.0.0.1:8877/selection/analyze", payload, 22000);
            if (result != null && result.actions != null)
                UnicodeSpans.Convert(result.display_text ?? text, result.actions);
            return result;
        }

        private LookupResponse LookupCore(
            string term,
            string context,
            bool refresh,
            string previousExplanation,
            bool instant,
            string detail = "full")
        {
            if (!instant) EnsureCloudConsent(true);
            Dictionary<string, string> payload = new Dictionary<string, string>();
            payload["term"] = term;
            payload["context"] = context ?? String.Empty;
            payload["refresh"] = refresh ? "true" : "false";
            payload["previous_explanation"] = refresh ? (previousExplanation ?? String.Empty) : String.Empty;
            payload["mode"] = instant ? "instant" : "full";
            payload["detail"] = detail;
            byte[] body = Encoding.UTF8.GetBytes(serializer.Serialize(payload));
            for (int attempt = 0; ; attempt++)
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8877/lookup");
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Timeout = 22000;
                request.ReadWriteTimeout = 22000;
                SetToken(request);
                request.ContentLength = body.Length;
                try
                {
                    using (Stream requestStream = request.GetRequestStream())
                        requestStream.Write(body, 0, body.Length);
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        LookupResponse result = serializer.Deserialize<LookupResponse>(reader.ReadToEnd());
                        if (result != null) UnicodeSpans.Convert(result.explanation, result.entities);
                        return result;
                    }
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

        public AnalyzeResponse AnalyzeHistoricalCaption(string text)
        {
            EnsureRunning();
            AnalyzeResponse result = PostJson<AnalyzeResponse>("http://127.0.0.1:8877/analyze",
                new Dictionary<string, string> { { "text", text }, { "mode", "model" },
                    { "difficulty", Difficulty } }, 22000);
            if (result != null)
            {
                UnicodeSpans.Convert(text, result.entities);
                UnicodeSpans.Convert(text, result.actions);
            }
            return result;
        }

        public CaptionTranslationResponse TranslateCaption(string text)
        {
            EnsureRunning();
            return PostJson<CaptionTranslationResponse>(
                "http://127.0.0.1:8877/caption/translate",
                new Dictionary<string, string> { { "text", text } }, 18000);
        }

        public AudioTranscriptionResponse TranscribeAudio(byte[] wav)
        {
            EnsureRunning();
            Dictionary<string, string> payload = new Dictionary<string, string>();
            payload["audio_base64"] = Convert.ToBase64String(wav);
            return PostJson<AudioTranscriptionResponse>(
                "http://127.0.0.1:8877/transcribe", payload, 30000);
        }

        public CalendarResponse ExportCalendar(Dictionary<string, object> payload)
        {
            EnsureRunning();
            object operation;
            bool checking = payload.TryGetValue("operation", out operation) && (string)operation == "check";
            if (operation is string && ((string)operation).StartsWith("outlook_", StringComparison.Ordinal))
                return PostJson<CalendarResponse>("http://127.0.0.1:8877/calendar/outlook", payload, 90000);
            if (operation as string == "clarify")
                return PostJson<CalendarResponse>("http://127.0.0.1:8877/calendar/clarify", payload);
            return PostJson<CalendarResponse>(checking ? "http://127.0.0.1:8877/calendar/check" :
                "http://127.0.0.1:8877/calendar/export", payload);
        }

        public void Log(string message)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            try { File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }

    }

    internal sealed class AnalysisRequestException : Exception
    {
        public AnalysisRequestException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

}
