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
    internal sealed class ScanResponse
    {
        public bool ok { get; set; }
        public string error { get; set; }
        public int duration_ms { get; set; }
        public int ocr_ms { get; set; }
        public int local_analysis_ms { get; set; }
        public int analysis_duration_ms { get; set; }
        public List<HighlightItem> highlights { get; set; }
        public List<OcrWord> words { get; set; }
        public string analysis_mode { get; set; }
    }

    internal sealed class ScanTiming
    {
        public int Generation { get; set; }
        public long CaptureMs { get; set; }
        public long FingerprintMs { get; set; }
        public long OcrMs { get; set; }
        public long LocalAnalysisMs { get; set; }
        public long RefinementMs { get; set; }
        public long RenderMs { get; set; }
        public long FirstVisibleMs { get; set; }
        public Stopwatch TotalWatch { get; private set; }

        public ScanTiming()
        {
            TotalWatch = new Stopwatch();
        }
    }

    internal sealed class LookupResponse
    {
        public string term { get; set; }
        public string explanation { get; set; }
        public List<AnalysisEntity> entities { get; set; }
        public List<string> sources { get; set; }
        public string lookup_mode { get; set; }
        public bool can_refresh { get; set; }
        public bool cached { get; set; }
        public bool needs_model { get; set; }
        public string error { get; set; }
    }

    internal sealed class CaptionTranslationResponse
    {
        public string translation { get; set; }
    }

    internal sealed class SelectionTerm
    {
        public string text { get; set; }
        public string explanation { get; set; }
    }

    internal sealed class SelectionAnalysisResponse
    {
        public bool analysis_cached { get; set; }
        public bool ok { get; set; }
        public string source_text { get; set; }
        public string display_text { get; set; }
        public bool ocr_corrected { get; set; }
        public string explanation { get; set; }
        public List<SelectionTerm> terms { get; set; }
        public List<AnalysisEntity> actions { get; set; }
        public string action_status { get; set; }
        public string analysis_mode { get; set; }
        public bool can_retry { get; set; }
        public int duration_ms { get; set; }
        public string notice { get; set; }
        public string error { get; set; }
    }

    internal sealed class LookupView
    {
        public string term { get; set; }
        public string context { get; set; }
        public string explanation { get; set; }
        public List<AnalysisEntity> entities { get; set; }
        public List<string> sources { get; set; }
        public bool can_refresh { get; set; }
        public Rectangle anchor { get; set; }
    }

    internal sealed class SessionInfo
    {
        public string token { get; set; }
        public bool has_key { get; set; }
        public string product_id { get; set; }
        public int protocol_version { get; set; }
        public string app_version { get; set; }
    }

    internal sealed class KeyValidationResult
    {
        public bool ok { get; set; }
        public bool configured { get; set; }
        public string model { get; set; }
        public bool model_available { get; set; }
        public string message { get; set; }
    }

    internal sealed class ServiceHealth
    {
        public bool ok { get; set; }
        public string product_id { get; set; }
        public int protocol_version { get; set; }
        public string app_version { get; set; }
        public string text_base_url { get; set; }
        public string base_url { get; set; }
        public string configuration_warning { get; set; }
        public bool has_key { get; set; }
        public bool has_explanation_key { get; set; }
        public string model { get; set; }
        public string explanation_provider { get; set; }
        public string explanation_model { get; set; }
        public string analysis_provider { get; set; }
        public string analysis_model { get; set; }
        public string analysis_mode { get; set; }
        public string speech_model { get; set; }
        public int model_analysis_calls_last_hour { get; set; }
        public int model_analysis_limit_per_hour { get; set; }
        public int analysis_cache_entries { get; set; }
    }

    internal sealed class OperationResponse
    {
        public bool ok { get; set; }
    }

    internal sealed class AudioTranscriptionResponse
    {
        public bool ok { get; set; }
        public string text { get; set; }
        public string raw_text { get; set; }
        public int term_corrections { get; set; }
        public string model { get; set; }
        public string error { get; set; }
        public bool retryable { get; set; }
    }

    internal sealed class WindowsOcrResponse
    {
        public bool ok { get; set; }
        public bool ready { get; set; }
        public string request_id { get; set; }
        public string error { get; set; }
        public int decode_ms { get; set; }
        public int recognize_ms { get; set; }
        public int worker_ms { get; set; }
        public List<OcrWord> words { get; set; }
        public List<OcrWord> latin_words { get; set; }
    }

    internal sealed class OcrWord
    {
        public string text { get; set; }
        public double x { get; set; }
        public double y { get; set; }
        public double w { get; set; }
        public double h { get; set; }
    }

    internal sealed class AnalyzeResponse
    {
        public List<AnalysisEntity> entities { get; set; }
        public List<AnalysisEntity> actions { get; set; }
        public string analysis_mode { get; set; }
        public int analysis_duration_ms { get; set; }
    }

}
