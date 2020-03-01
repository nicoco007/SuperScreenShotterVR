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
        private ulong _overlayHandle = 0;
        private ScreenshotResult _lastScreenshotResult = null;
        private bool _isTakingScreenshot = false;
        private bool _shouldShutDown = false;
        
        public void Init()
        {
            var workerThread = new Thread(WorkerThread);
            workerThread.Start();
        }

        private void WorkerThread()
        {
            Thread.CurrentThread.IsBackground = true;
            while (true)
            {
                Thread.Sleep(10); // 100 Hz
                if (!_ovr.IsInitialized())
                {
                    _ovr.SetDebugLogAction((message) => {
                        Debug.WriteLine(message);
                    });
                    _ovr.Init();
                    Thread.Sleep(1000);
                }
                else
                {
                    if (!_initComplete)
                    {
                        // Screenshots
                        SetOutputFolder(_settings.Directory);
                        if(_settings.ReplaceShortcut) _isHookedForScreenshots = _ovr.HookScreenshots();
                        
                        // Input
                        _ovr.LoadAppManifest("./app.vrmanifest");
                        _ovr.LoadActionManifest("./actions.json");
                        _ovr.RegisterActionSet("/actions/screenshots");
                        _ovr.RegisterDigitalAction(
                            "/actions/screenshots/in/take_screenshot",
                            (data, handle) => { if (data.bState) ScreenshotTriggered(); }
                        );
                        _overlayHandle = _ovr.InitNotificationOverlay("SuperScreenShotterVR");
                        _currentAppId = _ovr.GetRunningApplicationId();

                        // Events
                        _ovr.RegisterEvent(EVREventType.VREvent_RequestScreenshot, (data) => { 
                            Debug.WriteLine("Screenshot requested");

                            // This happens after running TakeScreenshot() with no application running
                            // It leaves us with an error akin to ScreenshotAlreadyInProgress until
                            // we submit an empty result to Steam, for some reason, so here we are.
                            _ovr.SubmitScreenshotToSteam(new ScreenshotResult());
                            _isTakingScreenshot = false;
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_ScreenshotTriggered, (data) => {
                            Debug.WriteLine("Screenshot triggered");
                            if (_isHookedForScreenshots) ScreenshotTriggered();
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_ScreenshotTaken, (data) => {
                            Debug.WriteLine("Screenshot taken");
                            ScreenShotTaken();
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_ScreenshotFailed, (data) => {
                            Debug.WriteLine("Screenshot failed");
                            _isTakingScreenshot = false;
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_ScreenshotProgressToDashboard, (data) => {
                            Debug.WriteLine("Screenshot progress to dashboard");
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_SceneApplicationChanged, (data) => {
                            _currentAppId = _ovr.GetRunningApplicationId();

                            // At some occations we seem to lose the hook, so we redo it, seems fine.
                            if (_settings.ReplaceShortcut) _isHookedForScreenshots = _ovr.HookScreenshots();

                            Debug.WriteLine("New application running");
                        });
                        _ovr.RegisterEvent(EVREventType.VREvent_QuitAcknowledged, (data) =>
                        {
                            _ovr.AcknowledgeShutdown();
                            _shouldShutDown = true;
                        });

                        _initComplete = true;
                        Debug.WriteLine("Init complete.");
                    }
                    else
                    {
                        _ovr.UpdateActionStates();
                        _ovr.UpdateEvents();
                        ShutdownIfWeShould();
                        // TODO: keep track of timer here, if active.
                    }
                }
            }
        }

        internal void HookScreenshots()
        {
            if(_initComplete)
            {
                _isHookedForScreenshots = _ovr.HookScreenshots();
            }
        }

        public void SetOutputFolder(string path)
        {
            _ovr.SetScreenshotOutputFolder(path);
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

        private void ScreenshotTriggered()
        {
            if (_isTakingScreenshot)
            {
                Debug.WriteLine("We are already taking a screenshot!");
                return;
            }
            _isTakingScreenshot = true;

            var prefix = _currentAppId.StartsWith("steam.app.") ? _currentAppId.Split('.').Last() : _currentAppId;
            if(prefix != string.Empty)
            {
                // TODO: Check if output directory exists?
                // if (!Directory.Exists(_currentOutputPath)) Directory.CreateDirectory(_currentOutputPath);


                var success = _ovr.TakeScreenshot(out var result, prefix);
                _lastScreenshotResult = result;
                if (!success) _isTakingScreenshot = false;
            } else
            {
                Debug.WriteLine("No application is running.");
                _isTakingScreenshot = false;
            }
        }

        private void ScreenShotTaken()
        {
            var notificationBitmap = new NotificationBitmap_t();
            if (_lastScreenshotResult != null)
            {
                var filePath = $"{_lastScreenshotResult.filePath}.png";
                if(File.Exists(filePath))
                {
                    if(_settings.SubmitToSteam && _currentAppId != string.Empty)
                    {
                        var submitted = _ovr.SubmitScreenshotToSteam(_lastScreenshotResult);
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
            } else
            {
                Debug.WriteLine("Screenshot result was null.");
            }
            if(_settings.Notifications)
            {
                _ovr.EnqueueNotification(_overlayHandle, "Screenshot taken!", notificationBitmap);
            }
            _isTakingScreenshot = false;
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