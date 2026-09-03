using System;
using System.Drawing;
using System.Windows;
using QScreen.Recorder;

namespace QScreen
{
    public sealed partial class AppController
    {
        private Icon? _recIcon;

        public bool IsRecording => ScreenRecorder.Shared.IsRecording;

        private void InitRecorder()
        {
            ScreenRecorder.Shared.OnStateChange = rec =>
            {
                UpdateTrayMenu(rec);
                _tray.Icon = rec ? RecordingIcon() : AppIconProvider.GetAppIcon();
                _tray.Text = rec ? "QScreen — идёт запись" : $"QScreen v{UpdateChecker.CurrentVersion} ({UpdateChecker.BuildTag})";
            };
        }

        private Icon RecordingIcon()
        {
            if (_recIcon != null) return _recIcon;
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillEllipse(Brushes.Red, 4, 4, 24, 24);
                g.FillEllipse(Brushes.White, 11, 11, 10, 10);
            }
            _recIcon = Icon.FromHandle(bmp.GetHicon());
            return _recIcon;
        }

        // Старт/стоп на одном хоткее, как на маке
        public void RecordAreaAction()
        {
            if (ScreenRecorder.Shared.IsRecording) { ScreenRecorder.Shared.StopRecording(); return; }
            if (ScreenRecorder.Shared.IsArmed) { ScreenRecorder.Shared.BeginCapture(); return; }
            ResetPending();
            OverlayManager.ShowRecordOverlay(rect => ScreenRecorder.Shared.StartRecording(rect));
        }

        public void StopRecordingAction() => ScreenRecorder.Shared.StopRecording();
        public void TogglePauseAction() => ScreenRecorder.Shared.TogglePause();
    }
}
