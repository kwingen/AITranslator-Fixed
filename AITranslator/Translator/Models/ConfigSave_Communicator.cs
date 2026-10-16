using AITranslator.View.Models;
using AITranslator.Security;
using Newtonsoft.Json;
using System.Runtime.Serialization;
using AITranslator.View.UserControls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AITranslator.Translator.Models
{
    [JsonConverter(typeof(CredentialConfigReader))]
    internal class ConfigSave_Communicator
    {
        /// <summary>
        /// 模型通讯器类型
        /// </summary>
        [DefaultValue((CommunicatorType)(-1))]
        public CommunicatorType CommunicatorType { get; set; }

        #region LLama
        /// <summary>
        /// 本地LLM模型路径
        /// </summary>
        public string ModelPath { get; set; }
        /// <summary>
        /// 对话模板名词
        /// </summary>
        public string? InstructTemplateName { get; set; }
        /// <summary>
        /// GpuLayerCount
        /// </summary>
        public int GpuLayerCount { get; set; }
        /// <summary>
        /// ContextSize
        /// </summary>
        public uint ContextSize { get; set; }
        /// <summary>
        /// Flash Attention
        /// </summary>
        public bool FlashAttention { get; set; }
        /// <summary>
        /// 启动自动加载模型
        /// </summary>
        public bool AutoLoadModel { get; set; }
        #endregion

        #region OpenAI
        /// <summary>
        /// 模型名称
        /// </summary>
        public string Model { get; set; }
        /// <summary>
        /// API密钥
        /// </summary>
        private string apiKey = string.Empty;
        private string? apiKeyEndpoint;

        [JsonIgnore]
        public string ApiKey
        {
            get
            {
                if (apiKeyEndpoint != null && apiKeyEndpoint != CredentialEndpoint)
                    throw new CredentialProtectionException();
                return apiKey;
            }
            set
            {
                apiKey = value ?? string.Empty;
                apiKeyEndpoint = null;
                CredentialId = null;
            }
        }

        // Only this opaque identifier travels with a loader. DPAPI blobs stay in LocalAppData.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? CredentialId { get; private set; }

        [JsonProperty("ApiKey")]
        private string? LegacyApiKey { set => apiKey = value ?? string.Empty; }

        private string CredentialEndpoint
        {
            get
            {
                if (CommunicatorType != CommunicatorType.OpenAI ||
                    !Uri.TryCreate(ServerURL, UriKind.Absolute, out var endpoint) ||
                    (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp) ||
                    !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment) ||
                    ServerURL.Any(char.IsControl))
                    throw new CredentialProtectionException();
                // Preserve the exact endpoint, including path, query, scheme and port. Never rebind a key.
                return ServerURL;
            }
        }

        [OnSerializing]
        private void PersistApiKey(StreamingContext context)
        {
            if (string.IsNullOrEmpty(ApiKey))
                CredentialId = null;
            else if (CredentialId == null)
            {
                CredentialId = LocalCredentialStore.Save(apiKey, CredentialEndpoint);
                apiKeyEndpoint = CredentialEndpoint;
            }
        }

        [OnDeserialized]
        private void RestoreApiKey(StreamingContext context)
        {
            if (CredentialId != null)
            {
                apiKey = LocalCredentialStore.Load(CredentialId, CredentialEndpoint);
                apiKeyEndpoint = CredentialEndpoint;
            }
        }
        /// <summary>
        /// 翻译服务的访问URL
        /// </summary>
        public string ServerURL { get; set; }

        /// <summary>
        /// 额外的参数
        /// </summary>
        public string ExpendedParams { get; set; }
        #endregion


        public void CopyFromViewModel(ViewModel_Communicator vm)
        {
            var snapshot = vm.CaptureCredentialSnapshot();
            CommunicatorType = snapshot.CommunicatorType;
            switch (snapshot.CommunicatorType)
            {
                case CommunicatorType.LLama:
                    ModelPath = vm.ModelPath;
                    GpuLayerCount = vm.GpuLayerCount;
                    ContextSize = vm.ContextSize;
                    FlashAttention = vm.FlashAttention;
                    AutoLoadModel = vm.AutoLoadModel;
                    InstructTemplateName = vm.CurrentInstructTemplate?.Name;
                    break;
                case CommunicatorType.OpenAI:
                    Model = snapshot.Model;
                    ServerURL = snapshot.ServerURL;
                    ApiKey = snapshot.ApiKey;
                    ExpendedParams = snapshot.ExpendedParams;
                    break;
                default:
                    break;
            }
        }

        public void CopyToViewModel(ViewModel_Communicator vm)
        {
            vm.CommunicatorType = CommunicatorType;
            switch (CommunicatorType)
            {
                case CommunicatorType.LLama:
                    vm.ModelPath = ModelPath;
                    vm.GpuLayerCount = GpuLayerCount;
                    vm.ContextSize = ContextSize;
                    vm.FlashAttention = FlashAttention;
                    vm.AutoLoadModel = AutoLoadModel;
                    if (!string.IsNullOrWhiteSpace(InstructTemplateName))
                        vm.CurrentInstructTemplate = ViewModelManager.ViewModel.InstructTemplate.FirstOrDefault(s => s.Name == InstructTemplateName);
                    break;
                case CommunicatorType.OpenAI:
                    vm.Model = Model;
                    vm.ExpendedParams = ExpendedParams;
                    vm.RestoreCredentialSnapshot(CommunicatorType, ServerURL, ApiKey);
                    break;
                default:
                    break;
            }
        }

        public bool IsSame(ConfigSave_Communicator cfg)
        {
            if (CommunicatorType != cfg.CommunicatorType)
                return false;

            switch (CommunicatorType)
            {
                case CommunicatorType.LLama:
                    return  ModelPath == cfg.ModelPath &&
                            GpuLayerCount == cfg.GpuLayerCount &&
                            ContextSize == cfg.ContextSize &&
                            FlashAttention == cfg.FlashAttention &&
                            AutoLoadModel == cfg.AutoLoadModel &&
                            InstructTemplateName == cfg.InstructTemplateName;
                case CommunicatorType.OpenAI:
                    return Model == cfg.Model &&
                           ApiKey == cfg.ApiKey &&
                           ServerURL == cfg.ServerURL &&
                           ExpendedParams == cfg.ExpendedParams;
                default:
                    return false;
            }
        }
    }
}
