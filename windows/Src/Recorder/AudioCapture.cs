using System;
using NAudio.Wave;

namespace QScreen.Recorder
{
    /// <summary>Микрофон → PCM s16le 44.1k стерео. Muted = пишем тишину (чтобы не сбивать таймлайн), Paused = не пишем ничего.</summary>
    internal sealed class AudioCapture : IDisposable
    {
        private readonly WaveInEvent _wave;
        private readonly FfmpegEncoder _enc;
        private byte[] _silence = Array.Empty<byte>();
        public volatile bool Muted;
        public volatile bool Paused;

        public AudioCapture(FfmpegEncoder enc)
        {
            _enc = enc;
            _wave = new WaveInEvent { WaveFormat = new WaveFormat(FfmpegEncoder.AudioRate, 16, FfmpegEncoder.AudioChannels), BufferMilliseconds = 20 };
            _wave.DataAvailable += (s, e) =>
            {
                if (Paused || e.BytesRecorded == 0) return;
                if (Muted)
                {
                    if (_silence.Length < e.BytesRecorded) _silence = new byte[e.BytesRecorded];
                    _enc.WriteAudio(_silence, e.BytesRecorded);
                }
                else _enc.WriteAudio(e.Buffer, e.BytesRecorded);
            };
        }

        public static bool HasMicrophone() => WaveInEvent.DeviceCount > 0;

        public void Start() => _wave.StartRecording();

        public void Dispose()
        {
            try { _wave.StopRecording(); } catch { }
            _wave.Dispose();
        }
    }
}
