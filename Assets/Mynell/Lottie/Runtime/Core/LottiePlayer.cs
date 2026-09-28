using System;
using Mynell.Lottie.Eval;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Mynell.Lottie
{
    public enum LottieLoopMode
    {
        /// <summary>Play once and hold the last frame.</summary>
        Once,
        /// <summary>Repeat from the start.</summary>
        Loop,
        /// <summary>Alternate forward and backward.</summary>
        PingPong,
    }

    /// <summary>How the composition is mapped into the render target.</summary>
    public enum LottieScaleMode
    {
        /// <summary>Stretch the composition to the target (the target usually has the composition's aspect).</summary>
        Stretch,
        /// <summary>Uniform scale, whole composition visible (letterboxed).</summary>
        Fit,
        /// <summary>Uniform scale covering the target (cropped).</summary>
        Fill,
    }

    /// <summary>
    /// Playback and rendering of one animation instance into a <see cref="Texture2D"/> (premultiplied alpha, sRGB-encoded
    /// texels, no hardware sRGB decode — use the included Mynell/Lottie shaders). Rendering runs in Burst jobs scheduled by
    /// <see cref="LottieScheduler"/>; components (<see cref="LottieImage"/>, <see cref="LottieRenderer"/>) wrap this class.
    /// </summary>
    public sealed class LottiePlayer : IDisposable
    {
        LottieAsset _asset;
        LottieAnimation _animation;      // explicit animation (when no asset)
        LottieRenderState _state;
        Texture2D _texture;
        int _width, _height;
        float _frame;
        int _direction = 1;
        int _loopsDone;
        bool _registered;
        bool _pending;
        bool _dirty = true;
        bool _force = true;
        LottieAnimation _renderedAnimation;
        float _renderedFrame = float.NaN;
        int _renderedW, _renderedH;
        LottieScaleMode _renderedScale;
        bool _disposed;
        LottieOverrides _overrides;

        /// <summary>Playback speed multiplier (negative plays backwards).</summary>
        public float Speed = 1f;
        public LottieLoopMode LoopMode = LottieLoopMode.Loop;
        /// <summary>Number of loops before completing (Loop / PingPong); 0 = forever.</summary>
        public int LoopCount;
        /// <summary>Advance with unscaled time (ignores Time.timeScale).</summary>
        public bool UnscaledTime;
        /// <summary>Render fractional frames for smooth motion (lottie-web default). Off snaps to whole frames.</summary>
        public bool Subframes = true;
        /// <summary>When true the scheduler advances time automatically while playing.</summary>
        public bool AutoAdvance = true;
        public LottieScaleMode ScaleMode = LottieScaleMode.Stretch;
        /// <summary>Inactive players are neither advanced nor rendered by the scheduler (e.g. disabled components).</summary>
        public bool Active = true;

        /// <summary>Raised when a non-looping playback reaches its end (or the loop count is exhausted).</summary>
        public event Action<LottiePlayer> Completed;
        /// <summary>Raised every time a loop wraps around.</summary>
        public event Action<LottiePlayer> LoopCompleted;
        /// <summary>Raised on the main thread after a new frame was uploaded to <see cref="Texture"/>.</summary>
        public event Action<LottiePlayer> FrameRendered;

        public LottiePlayer() { }

        public LottiePlayer(LottieAsset asset) { Asset = asset; }

        /// <summary>The asset being played (null when an explicit <see cref="Animation"/> is used).</summary>
        public LottieAsset Asset
        {
            get => _asset;
            set
            {
                if (_asset == value && _animation == null) return;
                CompleteRender();
                _asset = value;
                _animation = null;
                _overrides?.Clear();
                ResetPlayback();
            }
        }

        /// <summary>The compiled animation currently played.</summary>
        public LottieAnimation Animation
        {
            get
            {
                if (_animation != null) return _animation.IsDisposed ? null : _animation;
                return _asset != null ? _asset.Animation : null;
            }
            set
            {
                CompleteRender();
                _animation = value;
                _asset = null;
                _overrides?.Clear();
                ResetPlayback();
            }
        }

        /// <summary>Render target (created on demand, <see cref="Width"/> × <see cref="Height"/>).</summary>
        public Texture2D Texture => _texture;
        public int Width => _width;
        public int Height => _height;
        public bool IsPlaying { get; private set; }
        public bool IsDisposed => _disposed;

        /// <summary>First frame of the playback range (absolute frame, inclusive).</summary>
        public float StartFrame { get; private set; }
        /// <summary>End of the playback range (absolute frame, exclusive).</summary>
        public float EndFrame { get; private set; }

        /// <summary>Current absolute frame (composition time, same units as keyframes).</summary>
        public float Frame
        {
            get => _frame;
            set
            {
                _frame = ClampFrame(value);
                _dirty = true;
            }
        }

        /// <summary>Time in seconds since the start of the playback range.</summary>
        public float Time
        {
            get
            {
                var a = Animation;
                return a != null && a.FrameRate > 0 ? (_frame - StartFrame) / a.FrameRate : 0;
            }
            set
            {
                var a = Animation;
                if (a != null) Frame = StartFrame + value * a.FrameRate;
            }
        }

        /// <summary>Progress 0..1 within the playback range.</summary>
        public float NormalizedTime
        {
            get
            {
                float len = EndFrame - StartFrame;
                return len > 0 ? math.saturate((_frame - StartFrame) / len) : 0;
            }
            set => Frame = StartFrame + math.saturate(value) * math.max(0, EndFrame - StartFrame - 1e-4f);
        }

        void ResetPlayback()
        {
            var a = Animation;
            StartFrame = a != null ? a.InPoint : 0;
            EndFrame = a != null ? a.OutPoint : 0;
            _frame = Speed >= 0 ? StartFrame : LastFrame();
            _direction = 1;
            _loopsDone = 0;
            _dirty = true;
        }

        float LastFrame() => math.max(StartFrame, EndFrame - 1);

        float ClampFrame(float f)
        {
            if (EndFrame <= StartFrame) return StartFrame;
            return math.clamp(f, StartFrame, math.max(StartFrame, EndFrame - 1e-4f));
        }

        // ------------------------------------------------------------------ playback control

        public void Play()
        {
            if (_disposed) return;
            if (!IsPlaying && LoopMode == LottieLoopMode.Once && IsAtEnd()) _frame = Speed >= 0 ? StartFrame : LastFrame();
            IsPlaying = true;
            Register();
        }

        public void Pause() => IsPlaying = false;

        /// <summary>Stops and rewinds to the start of the playback range.</summary>
        public void Stop()
        {
            IsPlaying = false;
            _direction = 1;
            _loopsDone = 0;
            Frame = Speed >= 0 ? StartFrame : LastFrame();
        }

        public void GoToAndStop(float frame)
        {
            IsPlaying = false;
            Frame = frame;
            Register();
        }

        public void GoToAndPlay(float frame)
        {
            Frame = frame;
            Play();
        }

        /// <summary>Restricts playback to [start, end) absolute frames.</summary>
        public void SetSegment(float start, float end)
        {
            var a = Animation;
            float lo = a != null ? a.InPoint : 0, hi = a != null ? a.OutPoint : 0;
            if (end < start) (start, end) = (end, start);
            StartFrame = math.clamp(start, lo, hi);
            EndFrame = math.clamp(end, lo, hi);
            Frame = _frame;
        }

        public void ClearSegment()
        {
            var a = Animation;
            SetSegment(a != null ? a.InPoint : 0, a != null ? a.OutPoint : 0);
        }

        public void PlaySegment(float start, float end)
        {
            SetSegment(start, end);
            _loopsDone = 0;
            _direction = 1;
            _frame = Speed >= 0 ? StartFrame : LastFrame();
            _dirty = true;
            Play();
        }

        /// <summary>Plays the range covered by a marker (marker time .. time + duration). Returns false if not found.</summary>
        public bool PlayMarker(string markerName)
        {
            var a = Animation;
            if (a == null) return false;
            int i = a.FindMarker(markerName);
            if (i < 0) return false;
            var m = a.GetMarker(i);
            float end = m.Duration > 0 ? m.Time + m.Duration : a.OutPoint;
            PlaySegment(m.Time, end);
            return true;
        }

        bool IsAtEnd() => Speed >= 0 ? _frame >= LastFrame() - 1e-4f : _frame <= StartFrame + 1e-4f;

        /// <summary>Advances playback by <paramref name="deltaTime"/> seconds.</summary>
        public void Advance(float deltaTime)
        {
            if (!IsPlaying || deltaTime <= 0) return;
            var a = Animation;
            if (a == null || a.FrameRate <= 0 || EndFrame <= StartFrame) return;
            float start = StartFrame, end = EndFrame, last = LastFrame();
            float len = end - start;
            float step = deltaTime * a.FrameRate * Speed * _direction;
            if (step == 0) return;
            float f = _frame + step;
            _dirty = true;
            switch (LoopMode)
            {
                case LottieLoopMode.Once:
                    if (f >= last && step > 0) { _frame = last; Complete(); return; }
                    if (f <= start && step < 0) { _frame = start; Complete(); return; }
                    _frame = f;
                    return;
                case LottieLoopMode.Loop:
                    if (step > 0 && f >= end)
                    {
                        if (LoopDone()) { _frame = last; Complete(); return; }
                        f = start + math.fmod(f - start, len);
                    }
                    else if (step < 0 && f < start)
                    {
                        if (LoopDone()) { _frame = start; Complete(); return; }
                        f = end - math.fmod(start - f, len);
                        if (f >= end) f = start;
                    }
                    _frame = ClampFrame(f);
                    return;
                default: // PingPong: one loop = there and back again
                {
                    int guard = 0;
                    while ((f > last || f < start) && guard++ < 64)
                    {
                        bool hitStart = f < start;
                        f = hitStart ? start + (start - f) : last - (f - last);
                        _direction = -_direction;
                        bool backAtOrigin = Speed >= 0 ? hitStart : !hitStart;
                        if (backAtOrigin && LoopDone()) { _frame = Speed >= 0 ? start : last; Complete(); return; }
                    }
                    _frame = ClampFrame(f);
                    return;
                }
            }
        }

        bool LoopDone()
        {
            _loopsDone++;
            LoopCompleted?.Invoke(this);
            return LoopCount > 0 && _loopsDone >= LoopCount;
        }

        void Complete()
        {
            IsPlaying = false;
            Completed?.Invoke(this);
        }

        // ------------------------------------------------------------------ rendering

        /// <summary>Sets the render target size in pixels (the texture is resized lazily on the main thread).</summary>
        public void SetSize(int width, int height)
        {
            width = math.clamp(width, 1, 8192);
            height = math.clamp(height, 1, 8192);
            if (width == _width && height == _height) return;
            _width = width;
            _height = height;
            _dirty = true;
            Register();
        }

        /// <summary>Forces the next frame to be re-rendered (and re-uploaded).</summary>
        public void SetDirty()
        {
            _dirty = true;
            _force = true;
            Register();
        }

        float RenderFrameValue => Subframes ? _frame : math.floor(_frame + 1e-3f);

        internal bool NeedsRender
        {
            get
            {
                if (_disposed || !Active || _width <= 0 || _height <= 0) return false;
                var a = Animation;
                if (a == null) return false;
                return _dirty || a != _renderedAnimation || _renderedFrame != RenderFrameValue ||
                       _renderedW != _width || _renderedH != _height || _renderedScale != ScaleMode;
            }
        }

        /// <summary>Creates / resizes the texture; true when its content was lost.</summary>
        bool EnsureTexture()
        {
            if (_texture == null)
            {
                _texture = new Texture2D(_width, _height, TextureFormat.RGBA32, false, true)
                {
                    name = "Lottie",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.DontSave,
                };
                return true;
            }
            if (_texture.width != _width || _texture.height != _height)
            {
                _texture.Reinitialize(_width, _height);
                return true;
            }
            return false;
        }

        Affine2 CompToDevice(LottieAnimation a)
        {
            switch (ScaleMode)
            {
                case LottieScaleMode.Fit:
                    return LottieRenderState.FitTransform(a.Width, a.Height, _width, _height, true);
                case LottieScaleMode.Fill:
                {
                    float s = math.max(_width / a.Width, _height / a.Height);
                    float tx = (_width - a.Width * s) * 0.5f, ty = (_height - a.Height * s) * 0.5f;
                    return new Affine2(s, 0, 0, -s, tx, _height - ty);
                }
                default:
                    return LottieRenderState.StretchTransform(a.Width, a.Height, _width, _height, true);
            }
        }

        /// <summary>Schedules the render job (main thread). Returns false when nothing needs rendering.</summary>
        internal bool ScheduleRender()
        {
            if (_pending || !NeedsRender) return false;
            var a = Animation;
            bool force = EnsureTexture() | _force | a != _renderedAnimation;
            _force = false;
            _state ??= new LottieRenderState { PersistentTarget = true };
            _state.Overrides = _overrides;
            if (force) _state.Invalidate();
            var pixels = _texture.GetRawTextureData<uint>();
            float frame = RenderFrameValue;
            _state.Schedule(a, frame, CompToDevice(a), pixels, _width, _height);
            _pending = true;
            _renderedAnimation = a;
            _renderedFrame = frame;
            _renderedW = _width;
            _renderedH = _height;
            _renderedScale = ScaleMode;
            _dirty = false;
            return true;
        }

        /// <summary>Waits for a scheduled render and uploads the texture.</summary>
        internal void CompleteRender()
        {
            if (!_pending) return;
            _state.Complete();
            _pending = false;
            // Identical frames (holds, paused content) keep the uploaded texture.
            if (!_state.LastFrameChanged || _texture == null) return;
            _texture.Apply(false, false);
            FrameRendered?.Invoke(this);
        }

        /// <summary>Renders the current frame synchronously (e.g. in edit mode).</summary>
        public void RenderNow()
        {
            if (!NeedsRender) return;
            if (ScheduleRender()) CompleteRender();
        }

        // ------------------------------------------------------------------ dynamic properties

        /// <summary>
        /// Overrides the color of the fills / strokes addressed by <paramref name="keyPath"/> (e.g. "Button/**/Fill 1",
        /// see <see cref="LottieKeyPath"/>). Returns the number of properties changed.
        /// </summary>
        public int SetColor(string keyPath, Color color) =>
            SetProperty(keyPath, LottieProperty.Color, new float4(color.r, color.g, color.b, 1f));

        /// <summary>Overrides fill / stroke / transform / layer opacity (0..1).</summary>
        public int SetOpacity(string keyPath, float opacity) =>
            SetProperty(keyPath, LottieProperty.Opacity, new float4(opacity, 0, 0, 0));

        /// <summary>Overrides stroke width (composition units).</summary>
        public int SetStrokeWidth(string keyPath, float width) =>
            SetProperty(keyPath, LottieProperty.StrokeWidth, new float4(width, 0, 0, 0));

        /// <summary>Overrides a layer / shape-group transform position (parent space, composition units).</summary>
        public int SetPosition(string keyPath, Vector2 position) =>
            SetProperty(keyPath, LottieProperty.Position, new float4(position.x, position.y, 0, 0));

        /// <summary>Overrides a layer / shape-group transform scale (1 = 100%).</summary>
        public int SetScale(string keyPath, Vector2 scale) =>
            SetProperty(keyPath, LottieProperty.Scale, new float4(scale.x, scale.y, 1, 0));

        /// <summary>Overrides a layer / shape-group transform rotation (degrees).</summary>
        public int SetRotation(string keyPath, float degrees) =>
            SetProperty(keyPath, LottieProperty.Rotation, new float4(degrees, 0, 0, 0));

        /// <summary>Sets a property override; the value uses the units of the typed setters (<see cref="SetColor"/> ...).</summary>
        public int SetProperty(string keyPath, LottieProperty property, Vector4 value)
        {
            var a = Animation;
            if (a == null || _disposed) return 0;
            var props = LottieKeyPath.ResolveProperties(a.Data, keyPath, property);
            if (props.Count == 0) return 0;
            CompleteRender();
            _overrides ??= new LottieOverrides();
            foreach (var p in props)
                _overrides.Set(a.Data.Props.Length, p.x, LottieKeyPath.ToLottieUnits(property, value, p.y));
            _dirty = true;
            Register();
            return props.Count;
        }

        /// <summary>Removes the overrides of a property (the animation drives it again). Returns the number removed.</summary>
        public int ClearProperty(string keyPath, LottieProperty property)
        {
            var a = Animation;
            if (a == null || _overrides == null) return 0;
            CompleteRender();
            int n = 0;
            foreach (var p in LottieKeyPath.ResolveProperties(a.Data, keyPath, property))
                if (_overrides.Remove(p.x)) n++;
            if (n > 0)
            {
                _dirty = true;
                Register();
            }
            return n;
        }

        /// <summary>Removes every property override.</summary>
        public void ClearOverrides()
        {
            if (_overrides == null || _overrides.IsEmpty) return;
            CompleteRender();
            _overrides.Clear();
            _dirty = true;
            Register();
        }

        /// <summary>All key paths of the current animation (layers, shape groups and items), useful to find names.</summary>
        public string[] GetKeyPaths()
        {
            var a = Animation;
            return a == null ? Array.Empty<string>() : LottieKeyPath.Enumerate(a.Data).ToArray();
        }

        void Register()
        {
            if (_registered || _disposed) return;
            LottieScheduler.Register(this);
            _registered = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            CompleteRender();
            if (_registered) LottieScheduler.Unregister(this);
            _registered = false;
            _state?.Dispose();
            _state = null;
            _overrides?.Dispose();
            _overrides = null;
            LottieUtil.DestroySafe(_texture);
            _texture = null;
            _disposed = true;
        }
    }
}
