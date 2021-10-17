using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.ExtendedExecution.Foreground;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System.Display;
using Windows.System.Threading;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace MJPEGStreamer
{
    public sealed partial class MainPage : Page
    {
        // Prevent the screen from sleeping while the camera is running
        private readonly DisplayRequest _displayRequest = new DisplayRequest();

        // For listening to media property changes
        private readonly SystemMediaTransportControls _systemMediaControls = SystemMediaTransportControls.GetForCurrentView();

        // MediaCapture and its state variables
        private MediaCapture _mediaCapture;
        private int _skipFrameCount;
        private MediaFrameReader _mediaFrameReader;
        private bool _isInitialized;
        private int _videoRotation = (int)VideoRotation.None;

        // UI state
        private bool _isSuspending;
        private bool _isActivePage;
        private bool _isUIActive;
        private Task _setupTask = Task.CompletedTask;

        // Information about the camera device

        ApplicationDataContainer _localSettings = ApplicationData.Current.LocalSettings;

        private int _currentVideoDeviceIndex = -1;

        private bool taskRunning = false;
        private DeviceInformationCollection _allVideoDevices;
        private InMemoryRandomAccessStream _jpegStreamBuffer;
        private bool _settingsPaneVisible;
        private double _imageQuality = 0.3;
        private bool _cameraComboBoxUserInteraction;
        private bool _mjpegStreamerInitialized;
        private bool _previewVideoEnabled = true;
        private int _httpServerPort = _defaultPort;
        private int _frameRate;
        private ThreadPoolTimer _periodicTimerStreamingStatus;
        private int _skippedFrames;
        private double _sourceFrameRate;
        private int _activityCounter;
        private bool _setupBasedOnState;
        private bool _mjpegStreamerIsInitializing;
        private ExtendedExecutionForegroundSession _extendedExecutionSession;
        private LowLagPhotoCapture _lowLagPhotoCapture;
        private Timer _periodicTimer;
        private const int _defaultPort = 8000;
        private MediaFrameSource _mediaFrameSource;

        #region Constructor, lifecycle and navigation

        public MainPage()
        {
            this.InitializeComponent();

            // Do not cache the state of the UI when suspending/navigating
            NavigationCacheMode = NavigationCacheMode.Disabled;

            //MJPEGStreamerInit();

        }

        private void Application_Suspending(object sender, SuspendingEventArgs e)
        {
            //Debug.WriteLine("###Suspending###");
            _isSuspending = true;

            try
            {
                var deferral = e.SuspendingOperation.GetDeferral();
                var task = Dispatcher.RunAsync(CoreDispatcherPriority.High, async () =>
                {
                    await SetUpBasedOnStateAsync();
                    deferral.Complete();
                });
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void Application_Resuming(object sender, object o)
        {
            //Debug.WriteLine("###Resuming###");
            _isSuspending = false;

            try
            {
                var task = Dispatcher.RunAsync(CoreDispatcherPriority.High, async () =>
                {
                    await SetUpBasedOnStateAsync();
                });
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            // Useful to know when to initialize/clean up the camera
            try
            {
                Application.Current.Suspending += Application_Suspending;
                Application.Current.Resuming += Application_Resuming;
                Window.Current.VisibilityChanged += Window_VisibilityChanged;

                _isActivePage = true;
                await SetUpBasedOnStateAsync();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            // Handling of this event is included for completenes, as it will only fire when navigating between pages and this sample only includes one page
            try
            {
                Application.Current.Suspending -= Application_Suspending;
                Application.Current.Resuming -= Application_Resuming;
                Window.Current.VisibilityChanged -= Window_VisibilityChanged;

                _isActivePage = false;
                await SetUpBasedOnStateAsync();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        #endregion Constructor, lifecycle and navigation

        string streamPassword
        {
            get
            {
                return Helpers.streamPassword;
            }
            set
            {
                Helpers.streamPassword = value;
            }
        }
        private async Task MJPEGStreamerInitAsync()
        {
            try
            {
                //Debug.WriteLine("Trying MJPEGStreamerInitAsync");
                if (_mjpegStreamerInitialized || _mjpegStreamerIsInitializing)
                {
                    return;
                }
                _mjpegStreamerInitialized = true;
                _mjpegStreamerIsInitializing = true;

                if (_mediaCapture == null)
                {
                    _mediaCapture = new MediaCapture();
                }


                //Debug.WriteLine("MJPEGStreamerInitAsync: now reading  settings");
                var portSetting = _localSettings.Values["HttpServerPort"];
                if (portSetting != null)
                {
                    TextBoxPort.Text = validatePortNumber(portSetting.ToString()).ToString();
                    _httpServerPort = (int)portSetting;
                }
                else
                {
                    TextBoxPort.Text = _defaultPort.ToString();
                    _httpServerPort = _defaultPort;
                }

                var videoRotationInt = _localSettings.Values["VideoRotation"];
                if (videoRotationInt != null)
                {
                    _videoRotation = (int)videoRotationInt;
                }

                var imageQualitySingle = _localSettings.Values["ImageQuality"];
                if (imageQualitySingle != null)
                {
                    _imageQuality = (double)imageQualitySingle;
                }
                else
                {
                    _imageQuality = (double)0.3;
                }
                ImageQualitySlider.Value = _imageQuality * 100;

                var frameRate = _localSettings.Values["FrameRate"];
                if (frameRate != null)
                {
                    _frameRate = (int)frameRate;
                }
                else
                {
                    _frameRate = (int)5;
                }
                FrameRateSlider.Value = _frameRate;



                var preview = _localSettings.Values["Preview"];
                if (preview != null)
                {
                    //Debug.WriteLine("preview setting found: " + (bool)preview);
                    _previewVideoEnabled = (bool)preview;
                }
                else
                {
                    //Debug.WriteLine("no Preview setting found, setting to default ON.");
                    _previewVideoEnabled = true;
                }
                PreviewToggleSwitch.IsOn = _previewVideoEnabled;
                UpdatePreviewState();


                _allVideoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
                var id = _localSettings.Values["CurrentVideoDeviceId"];
                if (id == null)
                {
                    id = "********no setting*****";
                }

                //Debug.WriteLine("_allVideoDevices read");

                _currentVideoDeviceIndex = -1;
                int count = 0;

                CameraComboBox.Items.Clear();
                foreach (DeviceInformation di in _allVideoDevices)
                {
                    CameraComboBox.Items.Add(di.Name);

                    if (di.Id.Equals(id.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        _currentVideoDeviceIndex = count;
                        CameraComboBox.SelectedIndex = count;
                        //Debug.WriteLine("CAMERA INDEX FOUND");
                    }
                    count++;

                    //Debug.WriteLine("Video Device: {0} ID:{1}", di.Name, di.Id);
                    var mediaFrameSourceGroup = await MediaFrameSourceGroup.FromIdAsync(di.Id);

                    foreach (var si in mediaFrameSourceGroup.SourceInfos)
                    {
                        //Debug.WriteLine("   " + si.MediaStreamType.ToString() + " " + si.SourceKind.ToString());
                    }

                }

                await StartServer();
                _mjpegStreamerIsInitializing = false;
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }

        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                ColumnSettings.Width = new GridLength(0);
                UpdateCaptureControls();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }

            setIPInfo();
        }

        private void setIPInfo()
        {
            var IP = GetFirstLocalIp();
            if (IP.Length > 0)
            {
                if (streamPassword.Length > 0)
                {
                    try
                    {
                        StreamingIPTextBox.Text = $"http://{IP}:{TextBoxPort.Text}?pass={streamPassword}";
                    }
                    catch (Exception ex)
                    {

                    }
                }
                else
                {
                    try
                    {
                        StreamingIPTextBox.Text = $"http://{IP}:{TextBoxPort.Text}";
                    }
                    catch (Exception ex)
                    {

                    }
                }
            }
        }
        private async void Window_VisibilityChanged(object sender, VisibilityChangedEventArgs args)
        {
            try
            {
                await SetUpBasedOnStateAsync();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private async void StreamingButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isServerStarted)
                {
                    await StopServer();

                }
                else
                {
                    await StartServer();
                }
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private async void ColorFrameReader_FrameArrivedAsync(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            try
            {
                if (_skippedFrames < _skipFrameCount)
                {
                    _skippedFrames++;
                    return;
                }
                _skippedFrames = 0;

                var mediaFrameReference = sender.TryAcquireLatestFrame();
                var videoMediaFrame = mediaFrameReference?.VideoMediaFrame;
                var softwareBitmap = videoMediaFrame?.SoftwareBitmap;

                if (softwareBitmap != null)
                {
                    var encoderId = BitmapEncoder.JpegEncoderId;

                    InMemoryRandomAccessStream jpegStream = new InMemoryRandomAccessStream();

                    var propertySet = new BitmapPropertySet();
                    var qualityValue = new BitmapTypedValue(_imageQuality, PropertyType.Single);

                    propertySet.Add("ImageQuality", qualityValue);
                    BitmapEncoder bitmapEncoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, jpegStream, propertySet);

                    bitmapEncoder.BitmapTransform.Rotation = IntToBitmapRotation(_videoRotation);



                    bitmapEncoder.SetSoftwareBitmap(softwareBitmap);
                    await bitmapEncoder.FlushAsync();


                    if (_activityCounter++ > 50)
                    {
                        //Debug.WriteLine(".");
                        _activityCounter = 0;
                    }

                    else
                        Debug.Write(".");

                    Interlocked.Exchange(ref _jpegStreamBuffer, jpegStream);

                    if (_previewVideoEnabled)
                    {
                        // Changes to XAML ImageElement must happen on UI thread through Dispatcher
                        var task = imageElement.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                            async () =>
                            {
                                // Don't let two copies of this task run at the same time.
                                if (taskRunning)
                                {
                                    return;
                                }
                                taskRunning = true;

                                try
                                {
                                    InMemoryRandomAccessStream imageElementJpegStream = _jpegStreamBuffer;
                                    if (imageElementJpegStream != null)
                                    {
                                        BitmapImage bitmapImage = new BitmapImage();
                                        await bitmapImage.SetSourceAsync(imageElementJpegStream);
                                        imageElement.Source = bitmapImage;
                                    }
                                }
                                catch (Exception imageElementException)
                                {
                                    //Debug.WriteLine("Image Element writing exception. " + imageElementException.Message);
                                }

                                taskRunning = false;
                            });
                    }
                }
                if (mediaFrameReference != null)
                {
                    mediaFrameReference.Dispose();
                }
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }

        }


        private async void MediaCapture_RecordLimitationExceeded(MediaCapture sender)
        {
            // This is a notification that recording has to stop, and the app is expected to finalize the recording

            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateCaptureControls());
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private async void MediaCapture_Failed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
        {
            //Debug.WriteLine("MediaCapture_Failed: (0x{0:X}) {1}", errorEventArgs.Code, errorEventArgs.Message);

            try
            {
                await ShutdownAsync();

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateCaptureControls());
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }


        private async Task InitializeCameraAsync()
        {

            try
            {
                if (_mediaCapture == null)
                {
                    _mediaCapture = new MediaCapture();
                }

                if (_allVideoDevices == null)
                    return;

                if (_allVideoDevices.Count == 0)
                {
                    //Debug.WriteLine("ERROR: no webcam found or available");
                    return;
                }

                Object storedVideoDeviceId = _localSettings.Values["CurrentVideoDeviceId"];
                if (storedVideoDeviceId == null)
                {
                    storedVideoDeviceId = _allVideoDevices[0].Id;
                    _localSettings.Values["CurrentVideoDeviceId"] = storedVideoDeviceId;
                    CameraComboBox.SelectedIndex = 0;
                    //Debug.WriteLine("INFO: no webcam configured. Choosing the first available: " + _allVideoDevices[0].Name);

                }
                else
                {
                    //Debug.WriteLine("Loaded from Settings - CurrentVideoDeviceId: " + storedVideoDeviceId.ToString());
                }

                var mediaFrameSourceGroup = await MediaFrameSourceGroup.FromIdAsync(storedVideoDeviceId.ToString());

                MediaFrameSourceInfo selectedMediaFrameSourceInfo = null;
                foreach (MediaFrameSourceInfo sourceInfo in mediaFrameSourceGroup.SourceInfos)
                {
                    if (sourceInfo.MediaStreamType == MediaStreamType.VideoRecord)
                    {
                        selectedMediaFrameSourceInfo = sourceInfo;
                        break;
                    }
                }
                if (selectedMediaFrameSourceInfo == null)
                {
                    //Debug.WriteLine("no compatible MediaSource found.");
                    return;
                }

                MediaCaptureInitializationSettings mediaSettings = new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = storedVideoDeviceId.ToString(),
                    SourceGroup = mediaFrameSourceGroup,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu
                };


                try
                {
                    await _mediaCapture.InitializeAsync(mediaSettings);
                }
                catch (Exception ex)
                {
                    //Debug.WriteLine("MediaCapture initialization failed: " + ex.Message);
                    return;
                }
                //Debug.WriteLine("MediaFrameCapture initialized");

                _mediaFrameSource = _mediaCapture.FrameSources[selectedMediaFrameSourceInfo.Id];

                imageElement.Source = new SoftwareBitmapSource();

                MediaRatio mediaRatio = _mediaFrameSource.CurrentFormat.VideoFormat.MediaFrameFormat.FrameRate;
                _sourceFrameRate = mediaRatio.Numerator / (double)mediaRatio.Denominator;
                CalculateFrameRate();

                _mediaFrameReader = await _mediaCapture.CreateFrameReaderAsync(_mediaFrameSource, MediaEncodingSubtypes.Argb32);
                try
                {
                    _mediaFrameReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                }
                catch (Exception ex)
                {

                }
                _mediaFrameReader.FrameArrived += ColorFrameReader_FrameArrivedAsync;
                await _mediaFrameReader.StartAsync();
                //Debug.WriteLine("MediaFrameReader StartAsync done ");

                //Debug.WriteLine("MediaFrameCapture Formats");
                CameraFormatBox.Items.Clear();
                foreach (var SupportedFormat in _mediaFrameSource.SupportedFormats)
                {
                    if (!CameraFormatBox.Items.Contains(SupportedFormat.Subtype))
                        CameraFormatBox.Items.Add(SupportedFormat.Subtype);
                    //Debug.WriteLine("" + SupportedFormat.Subtype + " " + SupportedFormat.VideoFormat.Height + " " + SupportedFormat.VideoFormat.Width + " " + SupportedFormat.FrameRate.Numerator / SupportedFormat.FrameRate.Denominator);
                }

                CameraFormatBox.SelectedValue = _mediaFrameSource.CurrentFormat.Subtype;
                CameraResolutionBox.SelectedValue = _mediaFrameSource.CurrentFormat.VideoFormat.Width + " x " + _mediaFrameSource.CurrentFormat.VideoFormat.Height;

                _isInitialized = true;
                UpdateCaptureControls();

            }
            catch (Exception ex)
            {
                ShowError(ex);
            }

        }

        private void CalculateAndSetFrameRate(UInt16 framerate)
        {
            try
            {
                if (framerate < 1 && framerate > 100)
                    return;

                _frameRate = framerate;
                CalculateFrameRate();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void CalculateFrameRate()
        {

            try
            {
                if (_sourceFrameRate < 1)
                {
                    _skipFrameCount = 0;
                }
                else
                {
                    _skipFrameCount = (int)(_sourceFrameRate / _frameRate - 0.5);
                }

                if (_skipFrameCount > 300 || _skipFrameCount < 0)
                {
                    _skipFrameCount = 0;
                }
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            //Debug.WriteLine("Target framerate: " + _frameRate + " Source framerate: " + _sourceFrameRate + " Skip frames count: " + _skipFrameCount);
        }

        /// <summary>
        /// Cleans up the camera resources (after stopping any video recording and/or preview if necessary) and unregisters from MediaCapture events
        /// </summary>
        /// <returns></returns>
        private async Task ShutdownAsync()
        {
            //Debug.WriteLine("ShutdownAsync: " + new System.Diagnostics.StackTrace().ToString());

            try
            {
                if (_isInitialized)
                {
                    // If a recording is in progress during cleanup, stop it 
                    if (_mediaFrameReader != null)
                    {
                        await _mediaFrameReader.StopAsync();
                        _mediaFrameReader.Dispose();
                    }

                    _mediaCapture.Dispose();
                    _mediaCapture = null;
                    _isInitialized = false;
                }
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }



        /// <summary>
        /// Initialize or clean up the camera and our UI,
        /// depending on the page state.
        /// </summary>
        /// <returns></returns>
        private async Task SetUpBasedOnStateAsync()
        {

            try
            {
                //Debug.WriteLine("Entering SetupBasedOnStateAsync(): " + new System.Diagnostics.StackTrace().ToString());
                // Avoid reentrancy: Wait until nobody else is in this function.
                while (!_setupTask.IsCompleted)
                {
                    await _setupTask;
                }

                // We want our UI to be active if
                // * We are the current active page.
                // * The window is visible.
                // * The app is not suspending.
                bool show = _isActivePage && Window.Current.Visible && !_isSuspending;

                if (_previewVideoEnabled != show)
                {
                    _previewVideoEnabled = show;
                    PreviewToggleSwitch.IsOn = show;
                }

                Func<Task> setupAsync = async () =>
                {
                    //if(!show)
                    if (_isSuspending)
                    {
                        //Debug.WriteLine("SetupBasedOnStateAsync - shutting down!");
                        _setupBasedOnState = false;
                        await ShutdownAsync();
                        await StopServer();
                    }
                    else if (!_setupBasedOnState)
                    {
                        //Debug.WriteLine("SetupBasedOnStateAsync - setting up!");
                        _setupBasedOnState = true;

                        ExtendedExecutionForegroundSession newSession = new ExtendedExecutionForegroundSession();
                        newSession.Reason = ExtendedExecutionForegroundReason.Unconstrained;
                        newSession.Description = "Long Running Processing";
                        newSession.Revoked += SessionRevoked;
                        ExtendedExecutionForegroundResult result = await newSession.RequestExtensionAsync();
                        switch (result)
                        {
                            case ExtendedExecutionForegroundResult.Allowed:
                                //Debug.WriteLine("Extended Execution in Foreground ALLOWED.");
                                _extendedExecutionSession = newSession;
                                break;

                            default:
                            case ExtendedExecutionForegroundResult.Denied:
                                //Debug.WriteLine("Extended Execution in Foreground DENIED.");
                                break;
                        }


                        await MJPEGStreamerInitAsync();
                        await InitializeCameraAsync();
                        await StartServer();

                        // Prevent the device from sleeping while running
                        _displayRequest.RequestActive();

                    }
                };
                //Debug.WriteLine("SetupBasedOnStateAsync -calling setup Async!");
                _setupTask = setupAsync();
                //Debug.WriteLine("SetupBasedOnStateAsync -setup Async called!");

                await _setupTask;

                //Debug.WriteLine("SetupBasedOnStateAsync - awaited setup task!");

                UpdateCaptureControls();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }

        }


        private async void SessionRevoked(object sender, ExtendedExecutionForegroundRevokedEventArgs args)
        {
            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    switch (args.Reason)
                    {
                        case ExtendedExecutionForegroundRevokedReason.Resumed:
                            //Debug.WriteLine("Extended execution revoked due to returning to foreground.");
                            break;

                        case ExtendedExecutionForegroundRevokedReason.SystemPolicy:
                            //Debug.WriteLine("Extended execution revoked due to system policy.");
                            break;
                    }

                    // EndExtendedExecution();
                });
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        /// <summary>
        /// This method will update the icons, enable/disable and show/hide the photo/video buttons depending on the current state of the app and the capabilities of the device
        /// </summary>
        private void UpdateCaptureControls()
        {
            try
            {
                if (_allVideoDevices == null || _mediaCapture == null)
                {
                    RotationButton.IsEnabled = false;
                    StreamingButton.IsEnabled = false;
                    WebcamButton.IsEnabled = false;
                    return;
                }

                RotationButton.IsEnabled = true;
                WebcamButton.IsEnabled = true;

                if (_allVideoDevices.Count > 0)
                {
                    StreamingButton.Opacity = 1.0;
                    StreamingButton.IsEnabled = true;
                    //StreamingButton.Foreground = new SolidColorBrush(Colors.LightGray);
                }
                else
                {
                    StreamingButton.IsEnabled = false;
                    StreamingButton.Opacity = 0.6;
                }

                NoWebCamErrorIcon.Visibility = _allVideoDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            }
            catch (Exception ex)
            {
                ShowError(ex);
            }

        }


        private async void CaptureImage_Clicked(object sender, RoutedEventArgs e)
        {
            try
            {
                var PicturesStorage = KnownFolders.PicturesLibrary;
                var SaveFolder = await PicturesStorage.CreateFolderAsync("MJPEGStreamer", CreationCollisionOption.OpenIfExists);
                if (SaveFolder != null)
                {
                    var _bitmap = new RenderTargetBitmap();
                    await _bitmap.RenderAsync(imageElement);
                    var fileName = DateTime.Now.ToString().Replace("/", "_").Replace("\\", "_").Replace(":", "_").Replace(" ", "_") + ".jpg";
                    var savefile = await SaveFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                    var pixels = await _bitmap.GetPixelsAsync();
                    using (IRandomAccessStream stream = await savefile.OpenAsync(FileAccessMode.ReadWrite))
                    {
                        var encoder = await
                        BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                        byte[] bytes = pixels.ToArray();
                        BitmapAlphaMode mode = BitmapAlphaMode.Ignore;
                        if (Path.GetExtension(fileName).ToLower().Equals(".png"))
                        {
                            mode = BitmapAlphaMode.Straight;
                        }
                        encoder.SetPixelData(BitmapPixelFormat.Bgra8,
                                                mode,
                                                (uint)_bitmap.PixelWidth,
                                            (uint)_bitmap.PixelHeight,
                                                96,
                                                96,
                                                bytes);

                        await encoder.FlushAsync();
                    }
                }
                else
                {
                    ShowError(new Exception("Cannot access to pictures folder!"));
                }
            }
            catch (Exception ex)
            {

            }
        }
        private void SettingsButton_Clicked(object sender, RoutedEventArgs e)
        {

            try
            {
                _settingsPaneVisible = !_settingsPaneVisible;
                if (_settingsPaneVisible)
                {
                    ColumnSettings.Width = new GridLength(220);
                }
                else
                {
                    ColumnSettings.Width = new GridLength(0);
                }

            }
            catch (Exception ex)
            {
                ShowError(ex);
            }

        }

        private async void WebcamButton_ClickedAsync(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_allVideoDevices == null)
                {
                    return;
                }

                if (_allVideoDevices.Count == 0)
                {
                    _currentVideoDeviceIndex = -1;
                    return;
                }

                _currentVideoDeviceIndex += 1;
                if (_currentVideoDeviceIndex >= _allVideoDevices.Count)
                {
                    _currentVideoDeviceIndex = 0;
                }
                SwitchCamera(_currentVideoDeviceIndex);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }

        }

        private async void SwitchCamera(int cameraIndex)
        {
            try
            {
                if (_allVideoDevices == null || _allVideoDevices.Count == 0)
                {
                    return;
                }

                if (cameraIndex < 0 || cameraIndex >= _allVideoDevices.Count)
                {
                    return;
                }

                _localSettings.Values["CurrentVideoDeviceId"] = _allVideoDevices[cameraIndex].Id;
                _currentVideoDeviceIndex = cameraIndex;
                CameraComboBox.SelectedIndex = cameraIndex;

                //Debug.WriteLine("Switched to device ID " + _allVideoDevices[cameraIndex].Id);

                await ShutdownAsync();
                await InitializeCameraAsync();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void RotationButton_Clicked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_mediaCapture == null)
                {
                    RotationButton.IsEnabled = false;
                    return;
                }
                _videoRotation += 1;
                if (_videoRotation > 3)
                {
                    _videoRotation = 0;
                }

                _localSettings.Values["VideoRotation"] = _videoRotation;
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        public BitmapRotation IntToBitmapRotation(int videoRotation)
        {
            switch (_videoRotation)
            {
                case 0: return BitmapRotation.None;
                case 1: return BitmapRotation.Clockwise90Degrees;
                case 2: return BitmapRotation.Clockwise180Degrees;
                case 3: return BitmapRotation.Clockwise270Degrees;
                default: return BitmapRotation.None;
            }
        }

        private async void TextBoxPort_LostFocusAsync(object sender, RoutedEventArgs e)
        {
            //Debug.WriteLine("TextBoxPort: " + TextBoxPort.Text);
            try
            {
                int port = validatePortNumber(TextBoxPort.Text);
                if (port != _httpServerPort)
                {
                    _httpServerPort = port;
                    await StopServer();
                    await StartServer();
                    _localSettings.Values["HttpServerPort"] = _httpServerPort;
                    TextBoxPort.Text = port.ToString();
                }
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private int validatePortNumber(String portString)
        {
            ushort port = _defaultPort;
            if (portString != null)
                UInt16.TryParse(portString, out port);
            return validatePortNumber(port);
        }

        private int validatePortNumber(int port)
        {
            if (port >= 80 && port < 65536)
            {
                return port;
            }
            return _defaultPort;
        }

        private void ImageQualitySlider_LostFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                _imageQuality = ImageQualitySlider.Value / 100.0;
                _localSettings.Values["ImageQuality"] = _imageQuality;
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void ImageQualitySlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            try
            {
                if (_imageQuality == ImageQualitySlider.Value)
                    return;
                _imageQuality = ImageQualitySlider.Value / 100.0;
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void CameraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (!_cameraComboBoxUserInteraction)
                    return;

                // reset the flag;
                _cameraComboBoxUserInteraction = false;

                int index = CameraComboBox.SelectedIndex;
                //Debug.WriteLine("SelectionChange Event " + index.ToString());
                if (index >= 0)
                {
                    SwitchCamera(index);
                    //Debug.WriteLine("SelectionChanged to " + CameraComboBox.SelectedItem.ToString());
                }
                else
                {
                    //Debug.WriteLine("SelectionChange event - no item selected");
                }
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }

        }

        private void CameraComboBox_LostFocus(object sender, RoutedEventArgs e)
        {

        }

        private void CameraComboBox_DropDownOpened(object sender, object e)
        {
            _cameraComboBoxUserInteraction = true;
        }

        private async void AboutNotesButton_ClickedAsync(object sender, RoutedEventArgs e)
        {
            try
            {
                ImportantNotesTextBlock.Text = "Streams USB and bulit-in camera video as MJPEG data over HTTP  " +
               "Created by flyinggorilla \r\n" +
               "Enhanced by Bashar Astifan\r\n" +
               "Check the Source code in the link below About";
                ImportantNotesContentDialog.Visibility = Visibility.Visible;
                await ImportantNotesContentDialog.ShowAsync();
                //Debug.WriteLine("Important Notes button clicked");
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }
        private async void ImportantNotesButton_ClickedAsync(object sender, RoutedEventArgs e)
        {
            try
            {
                ImportantNotesTextBlock.Text = "Note: Localhost loopback will not work! " +
               "You can only access this HTTP service from a remote computer due to Universal Windows App restrictions. \r\n" +
               "MJPEG streaming URL http://<hostname>:" + _httpServerPort + "/stream.mjpeg \r\n" +
               "JPG single image request http://<hostname>:" + _httpServerPort + "/image.jpg";
                ImportantNotesContentDialog.Visibility = Visibility.Visible;
                await ImportantNotesContentDialog.ShowAsync();
                //Debug.WriteLine("Important Notes button clicked");
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        bool DialogInProgress = false;
        private async void ShowError(Exception ex)
        {
            while (DialogInProgress)
            {
                await Task.Delay(1500);
            }
            try
            {
                DialogInProgress = true;
                ImportantNotesTextBlock.Text = ex.Message;
                ImportantNotesContentDialog.Visibility = Visibility.Visible;
                await ImportantNotesContentDialog.ShowAsync();
            }
            catch (Exception x)
            {

            }
            DialogInProgress = false;
        }
        private void FrameRateSlider_LostFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                _frameRate = (int)FrameRateSlider.Value;
                _localSettings.Values["FrameRate"] = _frameRate;
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void FrameRateSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            try
            {
                if (_frameRate == FrameRateSlider.Value)
                    return;
                _frameRate = (int)FrameRateSlider.Value;
                CalculateFrameRate();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void PreviewToggleSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdatePreviewState();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void UpdatePreviewState()
        {
            try
            {
                if (_periodicTimerStreamingStatus != null)
                    _periodicTimerStreamingStatus.Cancel();
                //Debug.WriteLine("UpdatePreviewState called - toggle: " + PreviewToggleSwitch.IsOn);
                if (PreviewToggleSwitch.IsOn)
                {
                    _previewVideoEnabled = true;
                    _localSettings.Values["Preview"] = _previewVideoEnabled;
                    imageElement.Visibility = Visibility.Visible;
                    //StreamingStatusTextBox.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _previewVideoEnabled = false;
                    _localSettings.Values["Preview"] = _previewVideoEnabled;
                    //StreamingStatusTextBox.Visibility = Visibility.Visible;
                    imageElement.Visibility = Visibility.Collapsed;
                }
                TimeSpan period = TimeSpan.FromSeconds(1);

                _periodicTimerStreamingStatus = ThreadPoolTimer.CreatePeriodicTimer(async (source) =>
                {
                    // Update the UI thread by using the UI core dispatcher.
                    //
                    await Dispatcher.RunAsync(CoreDispatcherPriority.High,
                        () =>
                        {
                            {
                                try
                                {
                                    StreamingStatusTextBox.Text = "Active streams: " + _activeStreams + "\r\n" + "JPEG size: " + ((long)_jpegStreamBuffer?.Size).ToFileSize();
                                }
                                catch (Exception ex)
                                {

                                }
                            }
                        });

                }, period);
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }

        }
        public static string GetFirstLocalIp(HostNameType hostNameType = HostNameType.Ipv4)
        {
            try
            {
                var icp = NetworkInformation.GetInternetConnectionProfile();

                if (icp?.NetworkAdapter == null) return null;
                var hostname =
                    NetworkInformation.GetHostNames()
                        .FirstOrDefault(
                            hn =>
                                hn.Type == hostNameType &&
                                hn.IPInformation?.NetworkAdapter != null &&
                                hn.IPInformation.NetworkAdapter.NetworkAdapterId == icp.NetworkAdapter.NetworkAdapterId);

                // the ip address
                return hostname?.CanonicalName;
            }catch(Exception ex)
            {

            }
            return "";
        }
        private string getIP()
        {
            try
            {
                foreach (HostName localHostName in NetworkInformation.GetHostNames())
                {
                    if (localHostName.IPInformation != null)
                    {
                        if (localHostName.Type == HostNameType.Ipv4)
                        {
                            return localHostName.ToString();
                        }
                    }
                }
            }catch(Exception e)
            {

            }
            return "";
        }
        private void imageElement_DoubleTapped(object sender, Windows.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            PreviewToggleSwitch.IsOn = !PreviewToggleSwitch.IsOn;
        }

        private void VideoPaneGrid_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                if (_settingsPaneVisible)
                {
                    if (e.OriginalSource == MJPEGStreamerGrid || e.OriginalSource == imageElement || e.OriginalSource == StreamingStatusTextBox)
                    {
                        SettingsButton_Clicked(sender, e);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void CameraResolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                FormatChange();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void CameraFormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                CameraResolutionBox.Items.Clear();
                foreach (var SupportedFormat in _mediaFrameSource.SupportedFormats)
                {
                    if (CameraFormatBox.SelectedValue.Equals(SupportedFormat.Subtype) && !CameraResolutionBox.Items.Contains(SupportedFormat.VideoFormat.Width + " x " + SupportedFormat.VideoFormat.Height))
                        CameraResolutionBox.Items.Add(SupportedFormat.VideoFormat.Width + " x " + SupportedFormat.VideoFormat.Height);
                }
                FormatChange();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private async void FormatChange()
        {
            try
            {
                if (CameraFormatBox.SelectedItem == null || CameraResolutionBox.SelectedItem == null) return;
                foreach (var SupportedFormat in _mediaFrameSource.SupportedFormats)
                {
                    if (SupportedFormat.Subtype.Equals(CameraFormatBox.SelectedItem))
                    {
                        if (CameraResolutionBox.SelectedItem.ToString().Contains(SupportedFormat.VideoFormat.Width.ToString()))
                        {
                            try
                            {
                                await _mediaFrameSource.SetFormatAsync(SupportedFormat);
                            }
                            catch (Exception ex)
                            {
                                //Debug.WriteLine("MediaCapture set format failed: " + ex.Message);
                                return;
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
        }

        private void CameraResolution_DropDownOpened(object sender, object e)
        {

        }

        private void StreamingIPTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            setIPInfo();
        }

        private void TextBoxPassword_TextChanged(object sender, TextChangedEventArgs e)
        {
            streamPassword = TextBoxPassword.Text;
            setIPInfo();
        }

        private void TextBoxPassword_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        {
            streamPassword = TextBoxPassword.Text;
            setIPInfo();
        }

        private void TextBoxPort_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        {

        }
    }
    public static class ExtensionMethods
    {
        public static string ToFileSize(this long l)
        {
            try
            {
                return String.Format(new FileSizeFormatProvider(), "{0:fs}", l);
            }
            catch (Exception e)
            {
                return "0 KB";
            }
        }
    }
    public class FileSizeFormatProvider : IFormatProvider, ICustomFormatter
    {
        public object GetFormat(Type formatType)
        {
            if (formatType == typeof(ICustomFormatter)) return this;
            return null;
        }

        private const string fileSizeFormat = "fs";
        private const Decimal OneKiloByte = 1024M;
        private const Decimal OneMegaByte = OneKiloByte * 1024M;
        private const Decimal OneGigaByte = OneMegaByte * 1024M;

        public string Format(string format, object arg, IFormatProvider formatProvider)
        {
            try
            {
                if (format == null || !format.StartsWith(fileSizeFormat))
                {
                    return defaultFormat(format, arg, formatProvider);
                }
            }
            catch (Exception e)
            {

            }
            if (arg is string)
            {
                return defaultFormat(format, arg, formatProvider);
            }

            Decimal size;

            try
            {
                size = Convert.ToDecimal(arg);
            }
            catch (Exception e)
            {
                return defaultFormat(format, arg, formatProvider);
            }

            string suffix;
            if (size > OneGigaByte)
            {
                size /= OneGigaByte;
                suffix = " GB";
            }
            else if (size > OneMegaByte)
            {
                size /= OneMegaByte;
                suffix = " MB";
            }
            else if (size > OneKiloByte)
            {
                size /= OneKiloByte;
                suffix = " KB";
            }
            else
            {
                suffix = " B";
            }

            string precision = format.Substring(2);
            if (String.IsNullOrEmpty(precision)) precision = "2";
            return String.Format("{0:N" + precision + "}{1}", size, suffix);

        }

        private static string defaultFormat(string format, object arg, IFormatProvider formatProvider)
        {
            IFormattable formattableArg = arg as IFormattable;
            if (formattableArg != null)
            {
                return formattableArg.ToString(format, formatProvider);
            }
            return arg.ToString();
        }

    }
}