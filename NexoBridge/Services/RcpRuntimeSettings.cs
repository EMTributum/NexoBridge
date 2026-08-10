using System;

namespace NexoBridge.Services
{
    public sealed class RcpRuntimeSettings
    {
        private static readonly string[] UsernameKeys = new[]
        {
            "RCP_NEXO_USERNAME",
            "NEXO_USERNAME",
            "NEXO_USER"
        };

        private static readonly string[] PasswordKeys = new[]
        {
            "RCP_NEXO_PASSWORD",
            "NEXO_PASSWORD",
            "NEXO_PASS"
        };

        private const string SourceUrlEnvName = "RCP_SOURCE_URL";

        public string GetResolvedUsername(string explicitUsername = null)
        {
            return FirstNonEmpty(explicitUsername, GetFirstEnvironmentValue(UsernameKeys));
        }

        public string GetResolvedPassword(string explicitPassword = null)
        {
            return FirstNonEmpty(explicitPassword, GetFirstEnvironmentValue(PasswordKeys));
        }

        public string GetResolvedSourceUrl(string explicitSourceUrl = null)
        {
            return FirstNonEmpty(explicitSourceUrl, Environment.GetEnvironmentVariable(SourceUrlEnvName));
        }

        public bool TryGetSourceUri(out Uri sourceUri)
        {
            string sourceUrl = GetResolvedSourceUrl();
            return Uri.TryCreate(sourceUrl, UriKind.Absolute, out sourceUri);
        }

        public bool HasAutomaticPollingConfiguration()
        {
            return TryGetSourceUri(out _)
                && !string.IsNullOrWhiteSpace(GetResolvedUsername())
                && !string.IsNullOrWhiteSpace(GetResolvedPassword());
        }

        private static string GetFirstEnvironmentValue(string[] keys)
        {
            foreach (string key in keys)
            {
                string value = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        private static string FirstNonEmpty(string primary, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(primary))
            {
                return primary.Trim();
            }

            return string.IsNullOrWhiteSpace(fallback)
                ? null
                : fallback.Trim();
        }
    }
}
