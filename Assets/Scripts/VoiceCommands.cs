  using System.Collections;                                                                                                                                        
  using System.Collections.Generic;                                                                                                                                
  using UnityEngine;                                                                                                                                               
  using UnityEngine.Android;                                                                                                                                       
   
  public class VoiceCommands : MonoBehaviour                                                                                                                       
  {               
      [Header("References")]
      public FaceScanner faceScanner;
                                                                                                                                                                   
      [Header("Settings")]
      public float listenCooldown = 1f;

      [Header("Mode (set by DeepLinkHandler)")]
      public bool isCorporate = false;   // adjusts voice prompts: "customer" vs "contact"                                                                                                                            
                  
      private AndroidJavaObject _speechRecognizer;                                                                                                                 
      private AndroidJavaObject _recognizerIntent;
      private bool _listening = false;                                                                                                                             
      private bool _ready = false;                                                                                                                                 
   
      // Pending add-friend state                                                                                                                                  
      private int    _pendingUnknownIndex = -1;
      private string _pendingName  = null;                                                                                                                         
      private string _pendingGroup = null;                                                                                                                         
      private enum AddStep { None, WaitingName, WaitingGroup, WaitingConfirm }
      private AddStep _addStep = AddStep.None;                                                                                                                     
                  
      void Start()                                                                                                                                                 
      {           
  #if UNITY_ANDROID && !UNITY_EDITOR
          if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))                                                                                      
              Permission.RequestUserPermission(Permission.Microphone);
          InitRecognizer();                                                                                                                                        
  #endif          
          _ready = true;                                                                                                                                           
      }           
                                                                                                                                                                   
      void InitRecognizer()
      {                                                                                                                                                            
          using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
          var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");                                                                              
          _recognizerIntent = new AndroidJavaObject("android.content.Intent",
              "android.speech.RecognizerIntent.ACTION_RECOGNIZE_SPEECH");                                                                                          
          _recognizerIntent.Call<AndroidJavaObject>("putExtra",
              "android.speech.RecognizerIntent.EXTRA_LANGUAGE_MODEL",                                                                                              
              "android.speech.RecognizerIntent.LANGUAGE_MODEL_FREE_FORM");
          _recognizerIntent.Call<AndroidJavaObject>("putExtra",                                                                                                    
              "android.speech.RecognizerIntent.EXTRA_MAX_RESULTS", 1);
      }                                                                                                                                                            
                  
      void Update()                                                                                                                                                
      {           
          if (!_ready || _listening) return;
          StartCoroutine(ListenOnce());
      }                                                                                                                                                            
   
      IEnumerator ListenOnce()                                                                                                                                     
      {           
          _listening = true;
          yield return new WaitForSeconds(listenCooldown);
                                                                                                                                                                   
  #if UNITY_ANDROID && !UNITY_EDITOR
          // Android speech recognition runs via a callback —                                                                                                      
          // results delivered to OnVoiceResult via UnitySendMessage                                                                                               
          using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");                                                                          
          var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");                                                                              
          var recognizer = new AndroidJavaObject(                                                                                                                  
              "android.speech.SpeechRecognizer",                                                                                                                   
              activity);                                                                                                                                           
          recognizer.Call("startListening", _recognizerIntent);
  #endif                                                                                                                                                           
                  
          _listening = false;                                                                                                                                      
      }           

      // Called by Android SpeechRecognizer callback via UnitySendMessage                                                                                          
      public void OnVoiceResult(string text)
      {                                                                                                                                                            
          if (string.IsNullOrEmpty(text)) return;
          text = text.ToLower().Trim();                                                                                                                            
          Debug.Log("Voice: " + text);
          ParseCommand(text);                                                                                                                                      
      }           
                                                                                                                                                                   
      void ParseCommand(string text)
      {
          // ── Multi-turn add flow ───────────────────────────────────────────
          if (_addStep == AddStep.WaitingName)
          {
              _pendingName = Capitalise(text);
              _addStep = AddStep.WaitingGroup;
              Speak(isCorporate ? "What department?" : "What group?");
              return;
          }
          if (_addStep == AddStep.WaitingGroup)
          {
              _pendingGroup = Capitalise(text);
              _addStep = AddStep.WaitingConfirm;
              Speak($"Save {_pendingName} to {_pendingGroup}?");
              return;
          }                                                                                                                                                        
          if (_addStep == AddStep.WaitingConfirm)                                                                                                                  
          {
              if (text.Contains("save") || text.Contains("yes") || text.Contains("confirm"))
              {
                  faceScanner.AddFriend(_pendingUnknownIndex, _pendingName, _pendingGroup);
                  string saved = isCorporate ? $"Customer {_pendingName} saved." : $"{_pendingName} saved.";
                  ResetAddFlow();
                  Speak(saved);
              }                                                                                                                                                    
              else if (text.Contains("cancel") || text.Contains("no"))
              {                                                                                                                                                    
                  ResetAddFlow();
                  Speak("Cancelled.");
              }                                                                                                                                                    
              return;
          }                                                                                                                                                        
                  
          // ── Single-shot commands ──────────────────────────────────────────                                                                                    
   
          // "scan"                                                                                                                                                
          if (text.Contains("scan"))
          {
              faceScanner.TriggerScan();
              return;
          }                                                                                                                                                        
   
          // "add unknown N, name, group, save"                                                                                                                    
          if (text.StartsWith("add unknown"))
          {                                                                                                                                                        
              var parts = text.Split(',');
              if (parts.Length >= 4 && parts[3].Trim().Contains("save"))                                                                                           
              {                                                                                                                                                    
                  int idx = ParseUnknownIndex(parts[0]);
                  string name  = Capitalise(parts[1].Trim());                                                                                                      
                  string group = Capitalise(parts[2].Trim());
                  faceScanner.AddFriend(idx, name, group);                                                                                                         
                  return;                                                                                                                                          
              }
              // Start multi-turn flow
              _pendingUnknownIndex = ParseUnknownIndex(text);
              _addStep = AddStep.WaitingName;
              Speak(isCorporate ? "What's the customer's name?" : "What's their name?");
              return;                                                                                                                                              
          }                                                                                                                                                        
   
          // "update name, group, save"                                                                                                                            
          if (text.StartsWith("update"))
          {
              var parts = text.Split(',');
              if (parts.Length >= 3 && parts[2].Trim().Contains("save"))
              {                                                                                                                                                    
                  string friendName = Capitalise(parts[0].Replace("update","").Trim());
                  string field      = parts[1].Trim(); // e.g. "group colleagues"                                                                                  
                  faceScanner.UpdateFriend(friendName, field);                                                                                                     
              }                                                                                                                                                    
              return;                                                                                                                                              
          }                                                                                                                                                        
                  
          // "stop"
          if (text.Contains("stop"))
          {
              faceScanner.StopScan();
              return;
          }                                                                                                                                                        
      }
                                                                                                                                                                   
      int ParseUnknownIndex(string text)
      {
          // Extract number from "add unknown 2" → 2 (1-based, convert to 0-based)
          foreach (var word in text.Split(' '))                                                                                                                    
              if (int.TryParse(word, out int n)) return n - 1;
          return 0;                                                                                                                                                
      }           
                                                                                                                                                                   
      void ResetAddFlow()
      {
          _addStep = AddStep.None;
          _pendingUnknownIndex = -1;                                                                                                                               
          _pendingName = null;
          _pendingGroup = null;                                                                                                                                    
      }           

      void Speak(string message)
      {
          Debug.Log("Glasses: " + message);
  #if UNITY_ANDROID && !UNITY_EDITOR                                                                                                                               
          using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
          var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");                                                                              
          var tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, null);                                                                      
          tts.Call<int>("speak", message, 0, null, null);                                                                                                          
  #endif                                                                                                                                                           
      }                                                                                                                                                            
                                                                                                                                                                   
      static string Capitalise(string s) =>
          string.IsNullOrEmpty(s) ? s :
          char.ToUpper(s[0]) + s.Substring(1).ToLower();                                                                                                           
  }
  