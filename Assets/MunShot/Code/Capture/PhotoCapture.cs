using System;
using Redux.UI.Screenshots;
using UnityEngine;

namespace MunShot.Capture
{
    /// <summary>
    /// Takes the photo with Redux's own screenshot (<see cref="ScreenshotCapture"/>): the same folder, image format,
    /// supersampling, HDR handling and wait for engine plumes as the game's screenshot key. Photo mode's own UI is
    /// hidden for the shot: <see cref="Started"/> asks for it to go, <see cref="Finished"/> brings it back.
    ///
    /// Redux doesn't say when a capture is done (it can wait up to 30 seconds for plumes), so this listens for the log
    /// line Redux writes when it has saved the file, or failed to, which also gives the file's path. If neither comes,
    /// the UI comes back after <see cref="TimeoutSeconds"/> anyway, and that's logged.
    /// </summary>
    public sealed class PhotoCapture
    {
        // Redux's own log lines (ScreenshotCapture.Announce / OnReadbackComplete)
        private const string SavedPrefix = "[Screenshot] Saved ";
        private const string FailedPrefix = "[Screenshot] The GPU readback for ";

        // Longer than Redux's own 30-second wait for plumes
        private const float TimeoutSeconds = 40f;

        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;
        private float _startedAt;

        public bool IsCapturing { get; private set; }

        /// <summary>Raised as a capture starts, before the frame is grabbed: hide photo mode's UI now.</summary>
        public event Action Started;

        /// <summary>Raised when the capture is over, with what to tell the player: show photo mode's UI again.</summary>
        public event Action<string> Finished;

        public PhotoCapture(Action<string> logInfo, Action<string> logError)
        {
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _logError = logError ?? throw new ArgumentNullException(nameof(logError));
        }

        /// <summary>
        /// Takes a photo. Also call it when the game's screenshot key is pressed in photo mode: Redux starts its own
        /// capture for the key, and this one then joins it (Redux ignores a second start while capturing) so the UI is
        /// hidden for it. The frame is grabbed at the end of a later frame, so hiding the UI now is in time.
        /// </summary>
        public void Take()
        {
            if (IsCapturing) return;
            ScreenshotCapture capture = ScreenshotCapture.Instance;
            if (capture == null || !capture.isActiveAndEnabled)
            {
                _logError("Photo mode: can't take a photo, Redux's screenshot capture isn't running.");
                Finished?.Invoke(Localization.Translate("MunShot/Status/CaptureNotRunning"));
                return;
            }

            IsCapturing = true;
            _startedAt = Time.unscaledTime;
            Application.logMessageReceived += OnLogMessage;
            Started?.Invoke();
            capture.Capture();
            _logInfo("Photo mode: taking a photo with Redux's screenshot capture; photo mode's UI is hidden until it's saved.");
        }

        /// <summary>Called every frame: gives up waiting for Redux after the timeout.</summary>
        public void Tick()
        {
            if (IsCapturing && Time.unscaledTime - _startedAt > TimeoutSeconds)
            {
                _logError($"Photo mode: Redux's screenshot didn't report saving within {TimeoutSeconds:0} seconds; "
                          + "showing photo mode's UI again. Check the Screenshots folder and the log for Redux's own messages.");
                End(Localization.Translate("MunShot/Status/MaybeNotSaved"));
            }
        }

        /// <summary>Stops waiting for a photo in progress (photo mode is closing); it's used again next time.</summary>
        public void Cancel()
        {
            if (IsCapturing) End(null);
        }

        private void OnLogMessage(string message, string stackTrace, LogType type)
        {
            if (message == null) return;
            if (message.StartsWith(SavedPrefix, StringComparison.Ordinal))
            {
                string path = message.Substring(SavedPrefix.Length);
                _logInfo($"Photo mode: photo saved to {path}");
                End(Localization.Format("MunShot/Status/Saved", path));
            }
            else if (message.StartsWith(FailedPrefix, StringComparison.Ordinal))
            {
                _logError($"Photo mode: the photo wasn't saved; Redux reported: {message}");
                End(Localization.Translate("MunShot/Status/NotSaved"));
            }
        }

        private void End(string message)
        {
            Application.logMessageReceived -= OnLogMessage;
            IsCapturing = false;
            Finished?.Invoke(message);
        }
    }
}
