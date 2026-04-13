using System;
using System.Collections;
using System.Text;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Streams the AR camera feed to the CrowdView MediaMTX server via WebRTC WHIP.
/// The stream appears in the Live Now tab of the Streams screen for friends watching
/// on the phone app — exactly the same as streaming from the phone camera.
///
/// Setup:
///   1. Attach to any persistent GameObject (e.g. same as DeepLinkHandler).
///   2. Assign streamCamera (the AR/main camera) in the Inspector.
///   3. DeepLinkHandler sets authToken after JWT receipt.
///   4. VoiceCommands calls StartStream() / StopStream().
/// </summary>
public class GlassesStreamer : MonoBehaviour
{
    [Header("References")]
    public Camera streamCamera;          // AR camera — rendered to a RenderTexture for the stream

    [Header("Server")]
    public string serverBase = "https://crowdview.tv";

    // Set by DeepLinkHandler after JWT arrives
    [HideInInspector] public string authToken;

    public bool IsStreaming { get; private set; }

    // ── Internals ─────────────────────────────────────────────────────────────
    private RTCPeerConnection _pc;
    private VideoStreamTrack  _videoTrack;
    private RenderTexture     _renderTex;
    private string            _streamKey;
    private string            _whipResourceUrl;
    private float             _pulseTime;

    [System.Serializable]
    private class StreamKeyResponse { public string streamKey; }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void OnDestroy()
    {
        if (IsStreaming) StopStream();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void StartStream()
    {
        if (IsStreaming || string.IsNullOrEmpty(authToken)) return;
        StartCoroutine(DoStart());
    }

    public void StopStream()
    {
        if (!IsStreaming) return;
        IsStreaming = false;
        StartCoroutine(DoStop());
    }

    // ── LIVE indicator overlay ────────────────────────────────────────────────

    void OnGUI()
    {
        if (!IsStreaming) return;

        _pulseTime += Time.deltaTime * 2.2f;
        float alpha = Mathf.Abs(Mathf.Sin(_pulseTime));   // 0 → 1 → 0 pulse

        float dotSize = Screen.width * 0.022f;
        float margin  = Screen.width * 0.022f;
        float x = Screen.width - margin - dotSize * 5.5f;
        float y = margin;

        // Pulsing red dot
        Color prev = GUI.color;
        GUI.color = new Color(1f, 0.12f, 0.12f, 0.5f + alpha * 0.5f);
        GUI.DrawTexture(new Rect(x, y + dotSize * 0.18f, dotSize, dotSize), Texture2D.whiteTexture);

        // "LIVE" text
        GUI.color = new Color(1f, 1f, 1f, 0.8f + alpha * 0.2f);
        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize  = Mathf.RoundToInt(dotSize),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
        };
        GUI.Label(new Rect(x + dotSize * 1.5f, y, dotSize * 4.5f, dotSize * 1.5f), "LIVE", style);

        GUI.color = prev;
    }

    // ── Start coroutine ───────────────────────────────────────────────────────

    IEnumerator DoStart()
    {
        // 1. Fetch stream key from server
        yield return FetchStreamKey();
        if (string.IsNullOrEmpty(_streamKey))
        {
            Debug.LogError("[GlassesStreamer] Could not fetch stream key — aborting");
            yield break;
        }

        // 2. Create a RenderTexture and point the AR camera at it
        _renderTex = new RenderTexture(1280, 720, 16, RenderTextureFormat.ARGB32);
        _renderTex.Create();
        if (streamCamera != null) streamCamera.targetTexture = _renderTex;

        // 3. Create video track from RenderTexture
        _videoTrack = new VideoStreamTrack(_renderTex);

        // 4. Create RTCPeerConnection
        var config = new RTCConfiguration
        {
            iceServers = new[]
            {
                new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } }
            }
        };
        _pc = new RTCPeerConnection(ref config);
        _pc.AddTrack(_videoTrack);

        // 5. Create SDP offer
        var offerOp = _pc.CreateOffer();
        yield return offerOp;
        if (offerOp.IsError)
        {
            Debug.LogError("[GlassesStreamer] CreateOffer failed: " + offerOp.Error.message);
            Cleanup(); yield break;
        }

        var desc = offerOp.Desc;
        var setLocalOp = _pc.SetLocalDescription(ref desc);
        yield return setLocalOp;
        if (setLocalOp.IsError)
        {
            Debug.LogError("[GlassesStreamer] SetLocalDescription failed: " + setLocalOp.Error.message);
            Cleanup(); yield break;
        }

        // 6. Wait for ICE gathering to complete (gather-and-send avoids trickle ICE complexity)
        float timeout = 8f;
        while (_pc.GatheringState != RTCIceGatheringState.Complete && timeout > 0f)
        {
            timeout -= Time.deltaTime;
            yield return null;
        }
        if (timeout <= 0f)
            Debug.LogWarning("[GlassesStreamer] ICE gather timeout — sending offer anyway");

        // 7. POST SDP offer to MediaMTX WHIP endpoint
        string whipUrl = $"{serverBase}/whip/live/{_streamKey}";
        var req = new UnityWebRequest(whipUrl, "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(_pc.LocalDescription.sdp));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type",  "application/sdp");
        req.SetRequestHeader("Authorization", "Bearer " + authToken);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[GlassesStreamer] WHIP POST failed ({req.responseCode}): {req.error} — {req.downloadHandler.text}");
            Cleanup(); yield break;
        }

        // Location header tells us where to send the WHIP DELETE later
        _whipResourceUrl = req.GetResponseHeader("Location");
        if (string.IsNullOrEmpty(_whipResourceUrl)) _whipResourceUrl = whipUrl;

        // 8. Apply the SDP answer from MediaMTX
        string answerSdp  = req.downloadHandler.text;
        var    answerDesc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = answerSdp };
        var setRemoteOp   = _pc.SetRemoteDescription(ref answerDesc);
        yield return setRemoteOp;
        if (setRemoteOp.IsError)
        {
            Debug.LogError("[GlassesStreamer] SetRemoteDescription failed: " + setRemoteOp.Error.message);
            Cleanup(); yield break;
        }

        IsStreaming = true;
        _pulseTime  = 0f;
        Debug.Log("[GlassesStreamer] Streaming live — key: " + _streamKey);
    }

    // ── Stop coroutine ────────────────────────────────────────────────────────

    IEnumerator DoStop()
    {
        // WHIP DELETE cleanly closes the session on MediaMTX, triggering on-unpublish
        if (!string.IsNullOrEmpty(_whipResourceUrl))
        {
            var req = new UnityWebRequest(_whipResourceUrl, "DELETE");
            req.SetRequestHeader("Authorization", "Bearer " + authToken);
            req.downloadHandler = new DownloadHandlerBuffer();
            yield return req.SendWebRequest();
        }
        Cleanup();
        Debug.Log("[GlassesStreamer] Stream stopped");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    IEnumerator FetchStreamKey()
    {
        var req = UnityWebRequest.Get(serverBase + "/api/stream/key");
        req.SetRequestHeader("Authorization", "Bearer " + authToken);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("[GlassesStreamer] FetchStreamKey failed: " + req.error);
            yield break;
        }

        var response = JsonUtility.FromJson<StreamKeyResponse>(req.downloadHandler.text);
        _streamKey = response?.streamKey;
        Debug.Log("[GlassesStreamer] Got stream key: " + _streamKey);
    }

    void Cleanup()
    {
        _videoTrack?.Dispose();
        _videoTrack = null;

        _pc?.Close();
        _pc?.Dispose();
        _pc = null;

        if (_renderTex != null)
        {
            if (streamCamera != null) streamCamera.targetTexture = null;
            _renderTex.Release();
            Destroy(_renderTex);
            _renderTex = null;
        }

        _streamKey       = null;
        _whipResourceUrl = null;
        IsStreaming      = false;
    }
}
