using LLama.Common;
using LLama.Sampling;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AITranslator.Translator.PostData
{
    public class PostDataNormal
    {
        public PostDataBase Base { get; set; } = new();
        public string Expended { get; set; } = string.Empty;
        internal bool StripLeadingThink { get; private set; }

        /// <summary>
        /// Accepts the existing comma-separated JSON property fragment or a complete object.
        /// A null property removes that default. An explicit max_completion_tokens replaces
        /// max_tokens; no token budget is inferred or increased.
        /// </summary>
        public override string ToString()
        {
            StripLeadingThink = false;
            JObject request = JObject.FromObject(Base);
            if (!string.IsNullOrWhiteSpace(Expended))
            {
                string text = Expended.Trim();
                JObject additions = JObject.Parse(text.StartsWith("{") ? text : "{" + text + "}",
                    new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                // Source/dialogue and model selection belong to the transport, not extensions.
                // Reject even null or identical values before applying any override.
                if (additions.Property("messages") is not null || additions.Property("model") is not null)
                    throw new JsonException("Transport-owned request properties cannot be extended.");
                JToken? reasoningMode = additions["_translator_reasoning"];
                if (reasoningMode is { Type: not JTokenType.Null })
                {
                    if (reasoningMode.Type != JTokenType.String ||
                        reasoningMode.Value<string>() is not ("reject" or "strip_leading_think"))
                        throw new JsonException("Invalid _translator_reasoning option.");
                    StripLeadingThink = reasoningMode.Value<string>() == "strip_leading_think";
                }
                additions.Remove("_translator_reasoning");
                foreach (JProperty property in additions.Properties())
                {
                    if (property.Value.Type == JTokenType.Null)
                        request.Remove(property.Name);
                    else
                        request[property.Name] = property.Value.DeepClone();
                }
                if (additions["max_completion_tokens"] is { Type: not JTokenType.Null })
                    request.Remove("max_tokens");
            }
            return request.ToString(Formatting.None);
        }

        public InferenceParams ToInferenceParams()
        {
            return new InferenceParams
            {
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = (float)Base.temperature,
                    FrequencyPenalty = (float)Base.frequency_penalty,
                    PresencePenalty = (float)Base.presence_penalty,
                    TopP = (float)Base.top_p,
                },

                AntiPrompts = Base.stop,
                MaxTokens = Base.max_tokens,
            };
        }
    }

}
