using System;
using System.Text;
using UnityEngine;

/// <summary>
/// Receives the JWT auth token from the CrowdView phone app via a deep link.
///
/// Phone (Capacitor) sends:   inmocrowdview://auth?token=&lt;JWT&gt;
/// Unity catches it here, pushes the token to FaceScanner, and decodes
/// the JWT payload to detect corporate mode (parentOrganizationId present).
///
/// Setup: attach to any persistent GameObject in the scene (e.g. AR Session Origin).
/// Assign faceScanner and voiceCommands in the Inspector.
/// </summary>
public class DeepLinkHandler : MonoBehaviour
{
    [Header("References")]
    public FaceScanner  faceScanner;
    public VoiceCommands voiceCommands;

    void Awake()
    {
        // Subscribe to deep links received while the app is already running.
        Application.deepLinkActivated += OnDeepLink;

        // Handle cold-start: OS launched the app with a deep link URL.
        if (!string.IsNullOrEmpty(Application.absoluteURL))
            OnDeepLink(Application.absoluteURL);
    }

    void OnDestroy()
    {
        Application.deepLinkActivated -= OnDeepLink;
    }

    // ── Deep link entry point ─────────────────────────────────────────────────

    void OnDeepLink(string url)
    {
        Debug.Log("[DeepLink] received: " + url);

        if (!url.StartsWith("inmocrowdview://auth", StringComparison.OrdinalIgnoreCase))
            return;

        string token = ExtractQueryParam(url, "token");
        if (string.IsNullOrEmpty(token))
        {
            Debug.LogWarning("[DeepLink] No token found in URL");
            return;
        }

        // Push token to FaceScanner.
        if (faceScanner != null)
            faceScanner.authToken = token;

        // Decode JWT payload to detect corporate mode.
        bool corporate = IsCorporateToken(token);
        int  orgId     = GetOrganizationId(token);

        if (faceScanner != null)
        {
            faceScanner.isCorporate      = corporate;
            faceScanner.organizationId   = orgId;
        }
        if (voiceCommands != null)
            voiceCommands.isCorporate = corporate;

        Debug.Log($"[DeepLink] auth applied — corporate={corporate}, orgId={orgId}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Extract a single query-string parameter from a URL.</summary>
    static string ExtractQueryParam(string url, string key)
    {
        int q = url.IndexOf('?');
        if (q < 0) return null;

        string query = url.Substring(q + 1);
        foreach (var pair in query.Split('&'))
        {
            var kv = pair.Split(new char[] { '=' }, 2);
            if (kv.Length == 2 && string.Equals(kv[0], key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    /// <summary>
    /// Returns true when the JWT payload contains a non-null, non-zero
    /// parentOrganizationId — the corporate-mode marker used by the server.
    /// </summary>
    static bool IsCorporateToken(string jwt)
    {
        string json = DecodeJwtPayload(jwt);
        if (json == null) return false;

        // Presence alone is not enough — must not be null or 0.
        int idx = json.IndexOf("\"parentOrganizationId\"", StringComparison.Ordinal);
        if (idx < 0) return false;

        int colon = json.IndexOf(':', idx);
        if (colon < 0) return false;

        string rest = json.Substring(colon + 1).TrimStart();
        if (rest.StartsWith("null", StringComparison.OrdinalIgnoreCase)) return false;
        if (rest.StartsWith("0"))   return false;   // zero = no org

        return true;
    }

    /// <summary>Returns the numeric parentOrganizationId from the JWT, or 0.</summary>
    static int GetOrganizationId(string jwt)
    {
        string json = DecodeJwtPayload(jwt);
        if (json == null) return 0;

        int idx = json.IndexOf("\"parentOrganizationId\"", StringComparison.Ordinal);
        if (idx < 0) return 0;

        int colon = json.IndexOf(':', idx);
        if (colon < 0) return 0;

        string rest = json.Substring(colon + 1).TrimStart();
        // Extract leading digits.
        int end = 0;
        while (end < rest.Length && char.IsDigit(rest[end])) end++;
        if (end == 0) return 0;

        return int.TryParse(rest.Substring(0, end), out int id) ? id : 0;
    }

    /// <summary>Base64url-decodes the JWT payload segment to a JSON string.</summary>
    static string DecodeJwtPayload(string jwt)
    {
        try
        {
            string[] parts = jwt.Split('.');
            if (parts.Length < 2) return null;

            string payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');

            // Pad to a multiple of 4.
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "=";  break;
            }

            byte[] bytes = Convert.FromBase64String(payload);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DeepLink] JWT decode failed: " + e.Message);
            return null;
        }
    }
}
