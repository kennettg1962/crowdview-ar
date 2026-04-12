  using System;
  using System.Collections;
  using System.Collections.Generic;
  using UnityEngine;
  using UnityEngine.XR.ARFoundation;
  using UnityEngine.XR.ARSubsystems;
  using Unity.Collections;
  using UnityEngine.Networking;
  using UnityEngine.UI;
  using TMPro;

  [Serializable] public class BoundingBox  { public float left, top, width, height; }
  [Serializable] public class TierData     { public string color; }
  [Serializable] public class FaceData     { public string faceId, friendId, status, friendName; public BoundingBox boundingBox; public TierData tier; }
  [Serializable] public class FaceResponse { public FaceData[] faces; }
  [Serializable] public class IdentifyRequest { public string imageData; }
  [Serializable] public class CreateFriendRequest  { public string name, group, note; }                                                                            
  [Serializable] public class CreateFriendResponse { public int friendId; }                                                                                        
  [Serializable] public class UpdateFriendRequest  { public string group, note; }

  public class FaceScanner : MonoBehaviour
  {
      [Header("AR")]
      public ARCameraManager cameraManager;

      [Header("UI")]
      public RectTransform overlayRect;

      [Header("API")]
      public string apiBaseUrl = "https://crowdview.tv";
      public string authToken = "";

      [Header("Scan Settings")]
      public float scanInterval = 2.5f;

      [Header("Mode (set by DeepLinkHandler)")]
      public bool isCorporate    = false;   // true when JWT contains parentOrganizationId
      public int  organizationId = 0;

      readonly List<GameObject> _boxes = new();
      bool _scanning;
      bool _paused;
      float _nextScan;

      FaceData[] _lastFaces;
      Texture2D  _lastTexture;

      void Update()
      {
          if (!_paused && !_scanning && Time.time >= _nextScan)
              StartCoroutine(ScanFrame());
      }

      public void TriggerScan() => StartCoroutine(ScanFrame());

      IEnumerator ScanFrame()
      {
          _scanning = true;
          _nextScan = Time.time + scanInterval;

          if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
          {
              _scanning = false;
              yield break;
          }

          int outW = cpuImage.width / 2;
          int outH = cpuImage.height / 2;

          var convParams = new XRCpuImage.ConversionParams
          {
              inputRect        = new RectInt(0, 0, cpuImage.width, cpuImage.height),
              outputDimensions = new Vector2Int(outW, outH),
              outputFormat     = TextureFormat.RGB24,
              transformation   = XRCpuImage.Transformation.MirrorY
          };

          var rawData = new NativeArray<byte>(
              cpuImage.GetConvertedDataSize(convParams), Allocator.Temp);
          cpuImage.Convert(convParams, rawData);
          cpuImage.Dispose();

          var tex = new Texture2D(outW, outH, TextureFormat.RGB24, false);
          tex.LoadRawTextureData(rawData);
          tex.Apply();
          rawData.Dispose();

          byte[] jpeg = tex.EncodeToJPG(75);
          if (_lastTexture != null) Destroy(_lastTexture);                                                                                                         
          _lastTexture = tex;                     
          // don't Destroy(tex) here — we keep it for face cropping                                                                                                

          string b64 = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);

          string body = JsonUtility.ToJson(new IdentifyRequest { imageData = b64 });
          byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(body);

          using var req = new UnityWebRequest(apiBaseUrl + "/api/rekognition/identify", "POST");
          req.uploadHandler   = new UploadHandlerRaw(bodyRaw);
          req.downloadHandler = new DownloadHandlerBuffer();
          req.SetRequestHeader("Content-Type",  "application/json");
          req.SetRequestHeader("Authorization", "Bearer " + authToken);

          yield return req.SendWebRequest();

          if (req.result == UnityWebRequest.Result.Success)
          {                                        
              var resp = JsonUtility.FromJson<FaceResponse>(req.downloadHandler.text);                                                                             
              _lastFaces = resp?.faces;                                               
              RenderOverlays(_lastFaces);                                                                                                                          
          }                              
          else
          {
              Debug.LogWarning("FaceScanner: " + req.error);
          }

          _scanning = false;
      }

      void RenderOverlays(FaceData[] faces)
      {
          foreach (var b in _boxes) Destroy(b);
          _boxes.Clear();

          if (faces == null || faces.Length == 0) return;

          float cw = overlayRect.rect.width;
          float ch = overlayRect.rect.height;
          int unk = 0;

          foreach (var face in faces)
          {
              var bb = face.boundingBox;
              Color col = StatusColor(face);

              // Box
              var boxGO = new GameObject("FaceBox");
              boxGO.transform.SetParent(overlayRect, false);
              var img = boxGO.AddComponent<Image>();
              img.color = new Color(0, 0, 0, 0);
              img.raycastTarget = false;
              var outline = boxGO.AddComponent<Outline>();
              outline.effectColor = col;
              outline.effectDistance = new Vector2(3, 3);
              var rt = boxGO.GetComponent<RectTransform>();
              rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
              rt.anchoredPosition = new Vector2(bb.left * cw, (1f - bb.top - bb.height) * ch);
              rt.sizeDelta = new Vector2(bb.width * cw, bb.height * ch);

              // Label
              string label = string.IsNullOrEmpty(face.friendName)
                  ? "Unknown " + (++unk) : face.friendName;
              var labelGO = new GameObject("Label");
              labelGO.transform.SetParent(boxGO.transform, false);
              var tmp = labelGO.AddComponent<TextMeshProUGUI>();
              tmp.text = label; tmp.fontSize = 14;
              tmp.color = Color.white;
              tmp.alignment = TextAlignmentOptions.Center;
              tmp.raycastTarget = false;
              var labelRt = labelGO.GetComponent<RectTransform>();
              labelRt.anchorMin = new Vector2(0, 0);
              labelRt.anchorMax = new Vector2(1, 0);
              labelRt.pivot = new Vector2(0.5f, 0);
              labelRt.anchoredPosition = Vector2.zero;
              labelRt.sizeDelta = new Vector2(0, 20);

              _boxes.Add(boxGO);
          }
      }

      static Color StatusColor(FaceData face)
      {
          if (face.tier != null && !string.IsNullOrEmpty(face.tier.color))
              if (ColorUtility.TryParseHtmlString(face.tier.color, out Color c)) return c;
          return face.status switch
          {
              "known"      => HexColor("#22c55e"),
              "identified" => HexColor("#f97316"),
              "employee"   => Color.white,
              _            => HexColor("#ef4444"),
          };
      }

      static Color HexColor(string hex)
      {
          ColorUtility.TryParseHtmlString(hex, out Color c);
          return c;
      }
 // ── Called by VoiceCommands ───────────────────────────────────────────────                                                                                    
                                                                                                                                                                   
      public void StopScan()
      {
          _scanning = false;
          _paused   = false;
          _nextScan = float.MaxValue;
          foreach (var b in _boxes) Destroy(b);
          _boxes.Clear();
          Debug.Log("FaceScanner: stopped");
      }

      public void PauseScan()
      {
          if (_paused) return;
          _paused   = true;
          _nextScan = float.MaxValue;
          // Hide overlay but keep _lastFaces so resume can redraw immediately.
          foreach (var b in _boxes) Destroy(b);
          _boxes.Clear();
          Debug.Log("FaceScanner: paused");
      }

      public void ResumeScan()
      {
          if (!_paused) return;
          _paused   = false;
          _nextScan = Time.time; // scan on next Update tick
          // Redraw last known faces immediately so there's no blank moment.
          if (_lastFaces != null && _lastFaces.Length > 0)
              RenderOverlays(_lastFaces);
          Debug.Log("FaceScanner: resumed");
      }

      public void AddFriend(int unknownIndex, string name, string group)                                                                                           
      {           
          StartCoroutine(AddFriendCoroutine(unknownIndex, name, group));
      }                                                                                                                                                            
   
      public void UpdateFriend(string friendName, string field)                                                                                                    
      {           
          StartCoroutine(UpdateFriendCoroutine(friendName, field));
      }                                                                                                                                                            
   
      IEnumerator AddFriendCoroutine(int unknownIndex, string name, string group)                                                                                  
      {           
          // Find the unknown face by index
          int unk = -1;                                                                                                                                            
          FaceData target = null;
          foreach (var face in _lastFaces)                                                                                                                         
          {                                                                                                                                                        
              if (string.IsNullOrEmpty(face.friendName)) unk++;
              if (unk == unknownIndex) { target = face; break; }                                                                                                   
          }       
                                                                                                                                                                   
          if (target == null)
          {
              Debug.LogWarning("AddFriend: unknown index not found");
              yield break;                                                                                                                                         
          }
                                                                                                                                                                   
          // POST /api/friends                                                                                                                                     
          string body = JsonUtility.ToJson(new CreateFriendRequest { name = name, group = group, note = "" });
          byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(body);                                                                                               
                  
          using var req = new UnityWebRequest(apiBaseUrl + "/api/friends", "POST");                                                                                
          req.uploadHandler   = new UploadHandlerRaw(bodyRaw);
          req.downloadHandler = new DownloadHandlerBuffer();                                                                                                       
          req.SetRequestHeader("Content-Type",  "application/json");
          req.SetRequestHeader("Authorization", "Bearer " + authToken);                                                                                            
          yield return req.SendWebRequest();
                                                                                                                                                                   
          if (req.result != UnityWebRequest.Result.Success)
          {
              Debug.LogWarning("AddFriend failed: " + req.error);
              yield break;                                                                                                                                         
          }
                                                                                                                                                                   
          var created = JsonUtility.FromJson<CreateFriendResponse>(req.downloadHandler.text);                                                                      
   
          // POST /api/friends/:id/photos — upload face crop                                                                                                       
          if (_lastTexture != null)
          {                                                                                                                                                        
              var bb = target.boundingBox;
              byte[] crop = CropFace(_lastTexture, bb);                                                                                                            
                  
              using var photoReq = new UnityWebRequest(                                                                                                            
                  apiBaseUrl + "/api/friends/" + created.friendId + "/photos", "POST");
              var form = new WWWForm();                                                                                                                            
              form.AddBinaryData("photo", crop, "face.jpg", "image/jpeg");
              photoReq.uploadHandler   = new UploadHandlerRaw(form.data);                                                                                          
              photoReq.downloadHandler = new DownloadHandlerBuffer();                                                                                              
              photoReq.SetRequestHeader("Authorization", "Bearer " + authToken);                                                                                   
              foreach (var h in form.headers)                                                                                                                      
                  photoReq.SetRequestHeader(h.Key, h.Value);
              yield return photoReq.SendWebRequest();                                                                                                              
          }                                                                                                                                                        
   
          // Update overlay — flip from "Unknown N" to name with green border                                                                                      
          target.friendName = name;
          target.status = "known";                                                                                                                                 
          RenderOverlays(_lastFaces);
          Debug.Log($"AddFriend: saved {name} to {group}");                                                                                                        
      }           
                                                                                                                                                                   
      IEnumerator UpdateFriendCoroutine(string friendName, string field)                                                                                           
      {
          // field format: "group colleagues"                                                                                                                      
          var parts = field.Split(' ');
          if (parts.Length < 2) yield break;
                                                                                                                                                                   
          string key   = parts[0].ToLower();   // e.g. "group"                                                                                                     
          string value = parts[1];             // e.g. "colleagues"                                                                                                
                                                                                                                                                                   
          // Find the face by name to get friendId
          FaceData target = null;
          foreach (var face in _lastFaces)                                                                                                                         
              if (!string.IsNullOrEmpty(face.friendName) &&
                  face.friendName.ToLower() == friendName.ToLower())                                                                                               
              { target = face; break; }                                                                                                                            
   
          if (target == null || string.IsNullOrEmpty(target.friendId))                                                                                             
          {       
              Debug.LogWarning("UpdateFriend: face not found — " + friendName);
              yield break;                                                                                                                                         
          }
                                                                                                                                                                   
          string body = key == "group"                                                                                                                             
              ? JsonUtility.ToJson(new UpdateFriendRequest { group = value })
              : JsonUtility.ToJson(new UpdateFriendRequest { note  = value });                                                                                     
                                                                                                                                                                   
          byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(body);
          using var req = new UnityWebRequest(                                                                                                                     
              apiBaseUrl + "/api/friends/" + target.friendId, "PATCH");
          req.uploadHandler   = new UploadHandlerRaw(bodyRaw);                                                                                                     
          req.downloadHandler = new DownloadHandlerBuffer();
          req.SetRequestHeader("Content-Type",  "application/json");                                                                                               
          req.SetRequestHeader("Authorization", "Bearer " + authToken);                                                                                            
          yield return req.SendWebRequest();
                                                                                                                                                                   
          if (req.result == UnityWebRequest.Result.Success)                                                                                                        
              Debug.Log($"UpdateFriend: {friendName} {key} → {value}");
          else                                                                                                                                                     
              Debug.LogWarning("UpdateFriend failed: " + req.error);
      }                                                                                                                                                            
   
      byte[] CropFace(Texture2D tex, BoundingBox bb)                                                                                                               
      {           
          int x = Mathf.FloorToInt(bb.left  * tex.width);
          int y = Mathf.FloorToInt((1f - bb.top - bb.height) * tex.height);                                                                                        
          int w = Mathf.FloorToInt(bb.width  * tex.width);                                                                                                         
          int h = Mathf.FloorToInt(bb.height * tex.height);                                                                                                        
          x = Mathf.Clamp(x, 0, tex.width  - 1);                                                                                                                   
          y = Mathf.Clamp(y, 0, tex.height - 1);                                                                                                                   
          w = Mathf.Clamp(w, 1, tex.width  - x);                                                                                                                   
          h = Mathf.Clamp(h, 1, tex.height - y);                                                                                                                   
          var crop = new Texture2D(w, h, TextureFormat.RGB24, false);
          crop.SetPixels(tex.GetPixels(x, y, w, h));                                                                                                               
          crop.Apply();
          return crop.EncodeToJPG(85);                                                                                                                             
      }           
  }
  
