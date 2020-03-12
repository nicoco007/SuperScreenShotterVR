﻿using BOLL7708;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Valve.VR;
using static BOLL7708.EasyOpenVRSingleton;

namespace SuperScreenShotterVR
{
    public class MainController
    {
        private Properties.Settings _settings = Properties.Settings.Default;
        private EasyOpenVRSingleton _ovr = EasyOpenVRSingleton.Instance;
        private bool _initComplete = false;
        private bool _isHookedForScreenshots = false;
        private string _currentAppId = "";
        private ulong _notificationOverlayHandle = 0;
        private Dictionary<uint, ScreenshotResult> _screenshotQueue = new Dictionary<uint, ScreenshotResult>();
        private bool _shouldShutDown = false;
        private MediaPlayer _mediaPlayer;
        private Thread _workerThread;
        private string _currentAudio = string.Empty;
        private Stopwatch _stopWatch = new Stopwatch();
        private const string VIEWFINDER_OVERLAY_UNIQUE_KEY = "boll7708.superscreenshottervr.overlay.viewfinder";
        private const string ROLL_INDICATOR_OVERLAY_UNIQUE_KEY = "boll7708.superscreenshottervr.overlay.rollindicator";
        private const string PITCH_INDICATOR_OVERLAY_UNIQUE_KEY = "boll7708.superscreenshottervr.overlay.pitchindicator";
        private const string RETICLE_OVERLAY_UNIQUE_KEY = "boll7708.superscreenshottervr.overlay.reticle";
        private ulong _viewfinderOverlayHandle = 0;
        private ulong _rollIndicatorOverlayHandle = 0;
        private ulong _pitchIndicatorOverlayHandle = 0;
        private ulong _reticleOverlayHandle = 0;
        private uint _trackedDeviceIndex = 0;
        private float _overlayWidth = 0;
        private OverlayTextureSize _reticleTextureSize = new OverlayTextureSize();

        // Actions
        public Action<bool> StatusUpdateAction { get; set; } = (status) => { Debug.WriteLine("No status action set."); };
        public Action<string> AppUpdateAction { get; set; } = (appId) => { Debug.WriteLine("No appID action set."); };

        public void Init()
        {
            // _ovr.SetApplicationType(EVRApplicationType.VRApplication_Overlay); // Did not fix the overlay unloading between games... ?!?!
            StatusUpdateAction.Invoke(false);
            AppUpdateAction.Invoke("");

            _workerThread = new Thread(WorkerThread);
            _workerThread.Start();
        }

        private void WorkerThread()
        {
            Thread.CurrentThread.IsBackground = true;
            while (true)
            {
                Thread.Sleep(1000/120); // TODO: Detect headset frame-rate changes?
                if (!_ovr.IsInitialized())
                {
                    _ovr.SetDebugLogAction((message) => {
                        Debug.WriteLine(message);
                    });
                    _ovr.Init();
                    StatusUpdateAction(_ovr.IsInitialized());
                    Thread.Sleep(1000);
                }
                else
                {
                    if (!_initComplete)
                    {
                        // Screenshots
                        UpdateScreenshotHook();
                        PlayScreenshotSound(true);
                        _currentAppId = _ovr.GetRunningApplicationId();
                        AppUpdateAction.Invoke(_currentAppId);
                        ToggleViewfinder(true); // DEBUG

                        // Input
                        _ovr.LoadAppManifest("./app.vrmanifest");
                        _ovr.LoadActionManifest("./actions.json");
                        _ovr.RegisterActionSet("/actions/screenshots");

                        UpdateTrackedDeviceIndex();

                        // TODO: After restart these do not get registered again??!!??
                        _ovr.RegisterDigitalAction(
                            "/actions/screenshots/in/take_screenshot",
                            (data, handle) => { if (data.bState) ScreenshotTriggered(); }
                        );
                        _ovr.RegisterDigitalAction(
                            "/actions/screenshots/in/show_viewfinder",
                            (data, handle) => { ToggleViewfinder(data.bState); }
                        );
                        _notificationOverlayHandle = _ovr.InitNotificationOverlay("SuperScreenShotterVR");
                        _currentAppId = _ovr.GetRunningApplicationId();

                        // Events
                        _ovr.RegisterEvent(EVREventType.VREvent_RequestScreenshot, (data) => { 
                            Debug.WriteLine("OBS! Screenshot request.");

                            // This happens after running TakeScreenshot() with no application running
                            // It leaves us with an error akin to ScreenshotAlreadyInProgress until
                            // we submit an empty result to Steam, we do that in ScreenShotTriggered().
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_ScreenshotTriggered, (data) => {
                            Debug.WriteLine($"Screenshot triggered, handle: {data.data.screenshot.handle}");
                            if (_isHookedForScreenshots) ScreenshotTriggered();
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_ScreenshotTaken, (data) => {
                            Debug.WriteLine($"Screenshot taken, handle: {data.data.screenshot.handle}");
                            ScreenShotTaken(data.data);
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_ScreenshotFailed, (data) => {
                            Debug.WriteLine("Screenshot failed");
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_ScreenshotProgressToDashboard, (data) => {
                            Debug.WriteLine("Screenshot progress to dashboard");
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_SceneApplicationChanged, (data) => {
                            _currentAppId = _ovr.GetRunningApplicationId();
                            AppUpdateAction.Invoke(_currentAppId);
                            _isHookedForScreenshots = false; // To enable rehooking
                            UpdateScreenshotHook(); // Hook at new application as it seems to occasionally get dropped
                            UpdateOutputFolder();                           
                            Debug.WriteLine($"New application running: {_currentAppId}");
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_QuitAcknowledged, (data) =>
                        {
                            _ovr.AcknowledgeShutdown();
                            _shouldShutDown = true;
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_TrackedDeviceActivated, (data) =>
                        {
                            UpdateTrackedDeviceIndex();
                        });

                        _initComplete = true;
                        Debug.WriteLine("Init complete.");
                    }
                    else
                    {
                        _ovr.UpdateActionStates();
                        _ovr.UpdateEvents();

                        if(_settings.CaptureTimer)
                        {
                            if (!_stopWatch.IsRunning) _stopWatch.Start();
                            if(_stopWatch.Elapsed.TotalSeconds >= _settings.TimerSeconds)
                            {
                                Debug.WriteLine("Timer triggered!");
                                // TODO: Capture screenshots without pushing notification? In that case, save separate list with handles?
                                ScreenshotTriggered();
                                _stopWatch.Restart();
                            }
                        } else if(_stopWatch.IsRunning)
                        {
                            _stopWatch.Stop();
                        }

                        UpdateOverlays();

                        ShutdownIfWeShould();
                    }
                }
            }
        }

        private void UpdateTrackedDeviceIndex()
        {
            var indexes = _ovr.GetIndexesForTrackedDeviceClass(ETrackedDeviceClass.HMD);
            if (indexes.Length > 0) _trackedDeviceIndex = indexes[0];
        }

        private void ToggleViewfinder(bool visible)
        {
            _viewfinderOverlayHandle = _ovr.FindOverlay(VIEWFINDER_OVERLAY_UNIQUE_KEY);
            _rollIndicatorOverlayHandle = _ovr.FindOverlay(ROLL_INDICATOR_OVERLAY_UNIQUE_KEY);
            _pitchIndicatorOverlayHandle = _ovr.FindOverlay(PITCH_INDICATOR_OVERLAY_UNIQUE_KEY);
            _reticleOverlayHandle = _ovr.FindOverlay(RETICLE_OVERLAY_UNIQUE_KEY);
            if (_viewfinderOverlayHandle == 0) _viewfinderOverlayHandle = CreateOverlay("viewfinder", VIEWFINDER_OVERLAY_UNIQUE_KEY, "SSSVRVF");
            if(_rollIndicatorOverlayHandle == 0) _rollIndicatorOverlayHandle = CreateOverlay("rollindicator", ROLL_INDICATOR_OVERLAY_UNIQUE_KEY, "SSSVRRI", true);
            if(_pitchIndicatorOverlayHandle == 0) _pitchIndicatorOverlayHandle = CreateOverlay("pitchindicator", PITCH_INDICATOR_OVERLAY_UNIQUE_KEY, "SSSVRPI", true);
            if (_reticleOverlayHandle == 0) _reticleOverlayHandle = CreateOverlay("reticle", RETICLE_OVERLAY_UNIQUE_KEY, "SSSVRR", true);
            var shouldBeVisible = visible && _settings.ViewFinder;
            _ovr.SetOverlayVisibility(_viewfinderOverlayHandle, shouldBeVisible);
            _ovr.SetOverlayVisibility(_rollIndicatorOverlayHandle, shouldBeVisible);
            _ovr.SetOverlayVisibility(_pitchIndicatorOverlayHandle, shouldBeVisible);
            _ovr.SetOverlayVisibility(_reticleOverlayHandle, shouldBeVisible);
        }

        private ulong CreateOverlay(string imageFileName, string uniqueKey, string title, bool small=false) {
            // Overlay
            var index = _trackedDeviceIndex;
            var distance = _settings.OverlayDistance;
            var overlayTransform = Utils.GetEmptyTransform();
            overlayTransform.m11 = -distance;
                
            // This assumes a square screenshot, might still work regardless but would need new texture.
            var fov = _ovr.GetFloatTrackedDeviceProperty(index, ETrackedDeviceProperty.Prop_ScreenshotHorizontalFieldOfViewDegrees_Float);
            var width = (float)Math.Tan(fov / 2f * Math.PI / 180) * distance * 2;
            _overlayWidth = width;
            if (small) width /= 4;

            // Instantiate overlay
            ulong handle = _ovr.CreateOverlay(uniqueKey, title, overlayTransform, width, index);

            // Apply texture
            var path = $"{Directory.GetCurrentDirectory()}\\resources\\{imageFileName}.png";
            _ovr.SetOverlayTextureFromFile(handle, path);
            return handle;
        }

        private void UpdateOverlays()
        {
            var poses = _ovr.GetDeviceToAbsoluteTrackingPose();
            if(poses.Length > _trackedDeviceIndex && _reticleTextureSize.aspectRatio != 0)
            {
                // TODO: Something is wrong in the whole YPR<->Quaternion<->Matrix flow as pitch and roll are flipped.
                var distance = _settings.OverlayDistance;
                var pose = poses[_trackedDeviceIndex];
                var hmdTransform = pose.mDeviceToAbsoluteTracking;
                var YPR = Utils.RotationMatrixToYPR(hmdTransform);

                var pitchYPR = new YPR { yaw = 0, pitch = 0, roll = 0 };
                var pitchTransform = Utils.GetTransformFromEuler(pitchYPR);
                var pitchY = (float) -YPR.roll * distance; // Y-pos, somehow this works
                var limitY = _overlayWidth / 8 / _reticleTextureSize.aspectRatio;
                if (pitchY > limitY) pitchY = limitY;
                if (pitchY < -limitY) pitchY = -limitY;
                pitchTransform.m7 = pitchY;
                pitchTransform.m11 = -distance;

                var rollYPR = new YPR { yaw = 0, pitch = -YPR.pitch, roll = 0 };
                var rollTransform = Utils.GetTransformFromEuler(rollYPR);
                rollTransform.m11 = -distance;

                if (_ovr.FindOverlay(PITCH_INDICATOR_OVERLAY_UNIQUE_KEY) != 0) _ovr.SetOverlayTransform(_pitchIndicatorOverlayHandle, pitchTransform, _trackedDeviceIndex);
                if (_ovr.FindOverlay(ROLL_INDICATOR_OVERLAY_UNIQUE_KEY) != 0) _ovr.SetOverlayTransform(_rollIndicatorOverlayHandle, rollTransform, _trackedDeviceIndex);
            } else
            {
                _reticleTextureSize = _ovr.GetOverlayTextureSize(_reticleOverlayHandle);
            }
        }

        public void UpdateScreenshotHook()
        {
            if(_ovr.IsInitialized() && _settings.ReplaceShortcut && !_isHookedForScreenshots)
            {
                _isHookedForScreenshots = _ovr.HookScreenshots();
                Debug.WriteLine($"Hooking for screenshots: {_isHookedForScreenshots}");
            }
        }

        public void UpdateOutputFolder(bool createDirIfNeeded=false)
        {
            if(_settings.Directory != string.Empty)
            {
                var dir = _settings.Directory;
                if (createDirIfNeeded && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if(_currentAppId != string.Empty)
                {
                    Debug.WriteLine($"Settings subfolder to: {_currentAppId}");
                    dir = $"{dir}\\{_currentAppId}";
                    if (createDirIfNeeded && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                _ovr.SetScreenshotOutputFolder(dir);
            } else
            {
                Debug.WriteLine("No output directory set.");
            }
        }

        private void ShutdownIfWeShould()
        {
            if(_shouldShutDown)
            {
                _shouldShutDown = false;
                _ovr.Shutdown();
                _initComplete = false;
            }
        }

        private float originalScale = 1;

        private void TakeScreenshot()
        {
            Debug.WriteLine("Taking screenshot!");
            originalScale = _ovr.GetRenderTargetForCurrentApp();
            _ovr.SetRenderScaleForCurrentApp(5f); // Clamped to 500%
            Thread.Sleep(100); // Needs at least 50ms to change render scale before taking screenshot
            var id = _ovr.GetRunningApplicationId();
            if (id != string.Empty) id = id.Split('.').Last();
            // _ovr.TakeScreenshot(id);
            _ovr.SetRenderScaleForCurrentApp(originalScale);
            Debug.WriteLine($"Screenshot taken! Original scale: {originalScale}");
        }

        private void PlayScreenshotSound(bool onlyLoad = false)
        {
            if(_mediaPlayer == null) _mediaPlayer = new MediaPlayer();
            if(_currentAudio != _settings.CustomAudio)
            {
                _currentAudio = _settings.CustomAudio;
                if(_currentAudio != string.Empty && File.Exists(_currentAudio))
                {
                    _mediaPlayer.Open(new Uri(_currentAudio));
                }
            }
            if(!onlyLoad)
            {
                _mediaPlayer.Stop();
                _mediaPlayer.Play();
            }
        }

        private void ScreenshotTriggered()
        {
            if(_currentAppId != string.Empty) // There needs to be a running application
            {
                Debug.WriteLine("Taking screenshot!");
                _ovr.SubmitScreenshotToSteam(new ScreenshotResult()); // To make sure we don't have any hanging requests
                UpdateOutputFolder(true); // Output folder
                var success = _ovr.TakeScreenshot(out var result); // Capture
                if (result != null) _screenshotQueue.Add(result.handle, result);
                if (success)
                {
                    if (_settings.Audio) PlayScreenshotSound(); // Sound effect
                } 
                else 
                {
                    Debug.WriteLine("Taking a screenshot failed");
                    _ovr.SubmitScreenshotToSteam(new ScreenshotResult()); // Will fix screenshot in progress limbo when spamming screenshots
                }
            } else Debug.WriteLine("No application is running");
        }

        private void ScreenShotTaken(VREvent_Data_t data)
        {
            var notificationBitmap = new NotificationBitmap_t();
            if (_screenshotQueue.ContainsKey(data.screenshot.handle))
            {
                ScreenshotResult result = _screenshotQueue[data.screenshot.handle];
                var filePath = $"{result.filePath}.png";
                if(File.Exists(filePath))
                {
                    if(_settings.SubmitToSteam && _currentAppId != string.Empty)
                    {
                        var submitted = _ovr.SubmitScreenshotToSteam(result);
                        Debug.WriteLine($"Managed to submit the screenshot to Steam: {submitted}");
                    } else Debug.WriteLine("Will not submit the screenshot to Steam.");

                    if(_settings.Notifications && _settings.Thumbnail)
                    {
                        var image = Image.FromFile(filePath);
                        var bitmap = ResizeImage(image, 256, 256);
                        SetAlpha(ref bitmap, 255);
                        notificationBitmap = BitmapUtils.NotificationBitmapFromBitmap(bitmap, true);
                    }
                } else
                {
                    Debug.WriteLine($"Could not find screenshot after taking it: {filePath}");
                }
                _screenshotQueue.Remove(data.screenshot.handle);
            } else
            {
                Debug.WriteLine($"Screenshot handle does not exist in queue? Handle: {data.screenshot.handle}");
            }
            if(_settings.Notifications)
            {
                _ovr.EnqueueNotification(_notificationOverlayHandle, "Screenshot taken!", notificationBitmap);
            }
            Debug.WriteLine($"Screenshot taken done, handle: {data.screenshot.handle}");
        }

        // https://stackoverflow.com/a/24199315
        public static Bitmap ResizeImage(Image image, int width, int height)
        {
            var destRect = new Rectangle(0, 0, width, height);
            var destImage = new Bitmap(width, height);

            destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);

            using (var graphics = Graphics.FromImage(destImage))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (var wrapMode = new ImageAttributes())
                {
                    wrapMode.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
                }
            }

            return destImage;
        }

        // https://stackoverflow.com/a/6809677
        public static void SetAlpha(ref Bitmap bmp, byte alpha)
        {
            if (bmp == null) throw new ArgumentNullException("bmp");

            var data = bmp.LockBits(
                new Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Imaging.ImageLockMode.ReadWrite,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            var line = data.Scan0;
            var eof = line + data.Height * data.Stride;
            while (line != eof)
            {
                var pixelAlpha = line + 3;
                var eol = pixelAlpha + data.Width * 4;
                while (pixelAlpha != eol)
                {
                    System.Runtime.InteropServices.Marshal.WriteByte(
                        pixelAlpha, alpha);
                    pixelAlpha += 4;
                }
                line += data.Stride;
            }
            bmp.UnlockBits(data);
        }
    }
}
