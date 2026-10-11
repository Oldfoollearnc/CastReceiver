using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CastReceiver.Core.Security;

namespace CastReceiver.App.Services;

/// <summary>
/// 库内录像播放器：后台线程按帧率解密 + 解码，把已冻结的帧投给界面。
/// 全程在内存里完成 —— 播放过程中磁盘上不会出现任何明文帧（这也是不再用 MP4 的原因）。
/// </summary>
public sealed class VaultPlayback : IDisposable
{
    private readonly VaultItemReader _reader;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly object _gate = new();

    private bool _playing;
    private int _frame;
    private double _speed = 1.0;
    private bool _disposed;
    private int _pendingUiFrames;

    public VaultPlayback(VaultItemReader reader, Dispatcher dispatcher)
    {
        _reader = reader;
        _dispatcher = dispatcher;
        _loop = Task.Run(LoopAsync);
    }

    public event Action<BitmapSource, int>? FrameReady;
    public event Action<int, int>? Progress;
    public event Action<string>? Error;

    public int FrameCount => _reader.FrameCount;
    public int Fps => Math.Max(1, _reader.Info.Fps);
    public VaultItemInfo Info => _reader.Info;
    public int CorruptFrames { get; private set; }

    public bool IsPlaying { get { lock (_gate) return _playing; } }

    public int CurrentFrame { get { lock (_gate) return _frame; } }

    public double Speed
    {
        get { lock (_gate) return _speed; }
        set { lock (_gate) _speed = Math.Clamp(value, 0.25, 4.0); }
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_frame >= _reader.FrameCount) _frame = 0;
            _playing = true;
        }
    }

    public void Pause() { lock (_gate) _playing = false; }

    public void TogglePlay() { if (IsPlaying) Pause(); else Play(); }

    public void Seek(int frame)
    {
        lock (_gate) _frame = Math.Clamp(frame, 0, Math.Max(0, _reader.FrameCount - 1));
        RenderOnce(CurrentFrame);
    }

    public void Step(int delta) { Pause(); Seek(CurrentFrame + delta); }

    /// <summary>立即解码并投递某一帧（暂停状态下拖动进度条也走这里）。</summary>
    public void RenderOnce(int frame)
    {
        var image = DecodeFrame(frame, out string? error);
        if (image is null) { if (error is not null) Error?.Invoke(error); return; }
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            FrameReady?.Invoke(image, frame);
            Progress?.Invoke(frame, FrameCount);
        }));
    }

    private async Task LoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!IsPlaying) { await Task.Delay(25, token).ConfigureAwait(false); continue; }

                int index = CurrentFrame;
                if (index >= _reader.FrameCount)
                {
                    Pause();
                    await Task.Delay(50, token).ConfigureAwait(false);
                    continue;
                }

                var stopwatch = Stopwatch.StartNew();
                var image = DecodeFrame(index, out string? error);
                lock (_gate) _frame = index + 1;

                if (image is null)
                {
                    if (error is not null) _ = _dispatcher.BeginInvoke(new Action(() => Error?.Invoke(error)));
                    continue;
                }

                // 界面跟不上就丢帧，保证播放节奏不被拖慢
                if (Interlocked.Increment(ref _pendingUiFrames) <= 4)
                {
                    int captured = index;
                    _ = _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                    {
                        Interlocked.Decrement(ref _pendingUiFrames);
                        FrameReady?.Invoke(image, captured);
                        Progress?.Invoke(captured, FrameCount);
                    }));
                }
                else
                {
                    Interlocked.Decrement(ref _pendingUiFrames);
                }

                double frameMs = 1000.0 / (Fps * Speed);
                int delay = (int)Math.Round(frameMs - stopwatch.Elapsed.TotalMilliseconds);
                if (delay > 1) await Task.Delay(delay, token).ConfigureAwait(false);
                else await Task.Yield();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _ = _dispatcher.BeginInvoke(new Action(() => Error?.Invoke(ex.Message)));
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }

    private BitmapSource? DecodeFrame(int index, out string? error)
    {
        error = null;
        var bytes = _reader.GetFrame(index, out error);
        if (bytes is null)
        {
            CorruptFrames++;
            return null;
        }
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = new MemoryStream(bytes, writable: false);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            error = "解码失败：" + ex.Message;
            return null;
        }
    }

    /// <summary>把一帧解码成可保存/复制的位图（导出用，v1 不提供明文导出）。</summary>
    public static BitmapSource? DecodeBytes(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes, writable: false);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _loop.Wait(500); } catch { }
        try { _reader.Dispose(); } catch { }
        _cts.Dispose();
    }
}
