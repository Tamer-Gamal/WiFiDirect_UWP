using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace WiFiDirect_UWP
{
    public sealed partial class MainPage : Page
    {
        public ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; } = new ObservableCollection<DiscoveredDevice>();
        private ObservableCollection<ConnectedDevice> ConnectedDevices = new ObservableCollection<ConnectedDevice>();
        private WiFiDirectAdvertisementPublisher _publisher;
        private WiFiDirectConnectionListener _listener;
        private List<WiFiDirectInformationElement> _informationElements = new List<WiFiDirectInformationElement>();
        private ConcurrentDictionary<StreamSocketListener, WiFiDirectDevice> _pendingConnections = new ConcurrentDictionary<StreamSocketListener, WiFiDirectDevice>();
        private bool _fWatcherStarted = false;
        private DeviceWatcher _deviceWatcher;

        public MainPage()
        {
            this.InitializeComponent();
        }

        private void btnStartAdvertisement_Click(object sender, RoutedEventArgs e)
        {
            _publisher = new WiFiDirectAdvertisementPublisher();
            _publisher.StatusChanged += OnStatusChanged;

            _listener = new WiFiDirectConnectionListener();

            try
            {
                // This can raise an exception if the machine does not support WiFi. Sorry.
                _listener.ConnectionRequested += OnConnectionRequested;
            }
            catch (Exception ex)
            {
                //rootPage.NotifyUser($"Error preparing Advertisement: {ex}", NotifyType.ErrorMessage);
                return;
            }

            _publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;

            _publisher.Advertisement.IsAutonomousGroupOwnerEnabled = false;

            //// Legacy settings are meaningful only if IsAutonomousGroupOwnerEnabled is true.
            //if (_publisher.Advertisement.IsAutonomousGroupOwnerEnabled && chkLegacySetting.IsChecked.Value)
            //{
            //    _publisher.Advertisement.LegacySettings.IsEnabled = true;
            //    if (!string.IsNullOrEmpty(txtPassphrase.Text))
            //    {
            //        Windows.Security.Credentials.PasswordCredential creds = new Windows.Security.Credentials.PasswordCredential
            //        {
            //            Password = txtPassphrase.Text
            //        };
            //        _publisher.Advertisement.LegacySettings.Passphrase = creds;
            //    }

            //    if (!string.IsNullOrEmpty(txtSsid.Text))
            //    {
            //        _publisher.Advertisement.LegacySettings.Ssid = txtSsid.Text;
            //    }
            //}

            // Add the information elements.
            foreach (WiFiDirectInformationElement informationElement in _informationElements)
            {
                _publisher.Advertisement.InformationElements.Add(informationElement);
            }

            _publisher.Start();

            if (_publisher.Status == WiFiDirectAdvertisementPublisherStatus.Started)
            {
                //btnStartAdvertisement.IsEnabled = false;
                btnStopAdvertisement.IsEnabled = true;
                //rootPage.NotifyUser("Advertisement started.", NotifyType.StatusMessage);
            }
            else
            {
                // rootPage.NotifyUser($"Advertisement failed to start. Status is {_publisher.Status}", NotifyType.ErrorMessage);
            }
        }

        private async void OnConnectionRequested(WiFiDirectConnectionListener sender, WiFiDirectConnectionRequestedEventArgs args)
        {
            WiFiDirectConnectionRequest connectionRequest = args.GetConnectionRequest();
            bool success = await Dispatcher.RunTaskAsync(async () =>
            {
                return await HandleConnectionRequestAsync(connectionRequest);
            });

            if (!success)
            {
                // Decline the connection request
                //rootPage.NotifyUserFromBackground($"Connection request from {connectionRequest.DeviceInformation.Name} was declined", NotifyType.ErrorMessage);
                connectionRequest.Dispose();
            }
        }

        private async Task<bool> HandleConnectionRequestAsync(WiFiDirectConnectionRequest connectionRequest)
        {
            string deviceName = connectionRequest.DeviceInformation.Name;

            bool isPaired = (connectionRequest.DeviceInformation.Pairing?.IsPaired == true) ||
                            (await IsAepPairedAsync(connectionRequest.DeviceInformation.Id));

            //rootPage.NotifyUser($"Connecting to {deviceName}...", NotifyType.StatusMessage);

            // Pair device if not already paired and not using legacy settings
            if (!isPaired && !_publisher.Advertisement.LegacySettings.IsEnabled)
            {
                if (!await connectionSettingsPanel.RequestPairDeviceAsync(connectionRequest.DeviceInformation.Pairing))
                {
                    return false;
                }
            }

            WiFiDirectDevice wfdDevice = null;
            try
            {
                // IMPORTANT: FromIdAsync needs to be called from the UI thread
                wfdDevice = await WiFiDirectDevice.FromIdAsync(connectionRequest.DeviceInformation.Id);
            }
            catch (Exception ex)
            {
                //rootPage.NotifyUser($"Exception in FromIdAsync: {ex}", NotifyType.ErrorMessage);
                return false;
            }

            // Register for the ConnectionStatusChanged event handler
            //wfdDevice.ConnectionStatusChanged += OnConnectionStatusChanged;

            StreamSocketListener listenerSocket = new StreamSocketListener();

            // Save this (listenerSocket, wfdDevice) pair so we can hook it up when the socket connection is made.
            _pendingConnections[listenerSocket] = wfdDevice;

            IReadOnlyList<Windows.Networking.EndpointPair> EndpointPairs = wfdDevice.GetConnectionEndpointPairs();

            listenerSocket.ConnectionReceived += OnSocketConnectionReceived;
            try
            {
                await listenerSocket.BindEndpointAsync(EndpointPairs[0].LocalHostName, Globals.strServerPort);
            }
            catch (Exception ex)
            {
                //rootPage.NotifyUser($"Connect operation threw an exception: {ex.Message}", NotifyType.ErrorMessage);
                return false;
            }

            //rootPage.NotifyUser($"Devices connected on L2, listening on IP Address: {EndpointPairs[0].LocalHostName}" +
            //$" Port: {Globals.strServerPort}", NotifyType.StatusMessage);
            return true;
        }

        private void OnSocketConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            Windows.Foundation.IAsyncAction task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                //rootPage.NotifyUser("Connecting to remote side on L4 layer...", NotifyType.StatusMessage);
                StreamSocket serverSocket = args.Socket;

                SocketReaderWriter socketRW = new SocketReaderWriter(serverSocket, this);
                // The first message sent is the name of the connection.
                string message = await socketRW.ReadMessageAsync();

                // Find the pending connection and add it to the list of active connections.
                if (_pendingConnections.TryRemove(sender, out WiFiDirectDevice wfdDevice))
                {
                    ConnectedDevices.Add(new ConnectedDevice(message, wfdDevice, socketRW));
                }

                while (message != null)
                {
                    message = await socketRW.ReadMessageAsync();
                }
            });
        }

        private async Task<bool> IsAepPairedAsync(string deviceId)
        {
            List<string> additionalProperties = new List<string>
            {
                "System.Devices.Aep.DeviceAddress"
            };
            string deviceSelector = $"System.Devices.Aep.AepId:=\"{deviceId}\"";
            DeviceInformation devInfo = null;

            try
            {
                devInfo = await DeviceInformation.CreateFromIdAsync(deviceId, additionalProperties);
            }
            catch (Exception ex)
            {
                //rootPage.NotifyUser("DeviceInformation.CreateFromIdAsync threw an exception: " + ex.Message, NotifyType.ErrorMessage);
            }

            if (devInfo == null)
            {
                //rootPage.NotifyUser("Device Information is null", NotifyType.ErrorMessage);
                return false;
            }

            deviceSelector = $"System.Devices.Aep.DeviceAddress:=\"{devInfo.Properties["System.Devices.Aep.DeviceAddress"]}\"";
            DeviceInformationCollection pairedDeviceCollection = await DeviceInformation.FindAllAsync(deviceSelector, null, DeviceInformationKind.Device);
            return pairedDeviceCollection.Count > 0;
        }

        private async void OnStatusChanged(WiFiDirectAdvertisementPublisher sender, WiFiDirectAdvertisementPublisherStatusChangedEventArgs statusEventArgs)
        {
            if (statusEventArgs.Status == WiFiDirectAdvertisementPublisherStatus.Started)
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {

                });
            }

            //rootPage.NotifyUserFromBackground($"Advertisement: Status: {statusEventArgs.Status}, Error: {statusEventArgs.Error}", NotifyType.StatusMessage);
            return;
        }

        private void btnStopAdvertisement_Click(object sender, RoutedEventArgs e)
        {
            _publisher.Stop();
            _publisher.StatusChanged -= OnStatusChanged;

            _listener.ConnectionRequested -= OnConnectionRequested;

            connectionSettingsPanel.Reset();
            _informationElements.Clear();

            //btnStartAdvertisement.IsEnabled = true;
            btnStopAdvertisement.IsEnabled = false;
        }

        private async void btnSendMessage_Click(object sender, RoutedEventArgs e)
        {
            ConnectedDevice connectedDevice = (ConnectedDevice)lvConnectedDevices.SelectedItem;
            await connectedDevice.SocketRW.WriteMessageAsync(txtSendMessage.Text);
        }

        private bool CanCloseDevice(object connectedDevice)
        {
            return connectedDevice != null;
        }

        private void btnCloseDevice_Click(object sender, RoutedEventArgs e)
        {
            ConnectedDevice connectedDevice = (ConnectedDevice)lvConnectedDevices.SelectedItem;
            ConnectedDevices.Remove(connectedDevice);

            // Close socket and WiFiDirect object
            connectedDevice.Dispose();
        }

        private async void btnUnpair_Click(object sender, RoutedEventArgs e)
        {
            DiscoveredDevice discoveredDevice = (DiscoveredDevice)lvDiscoveredDevices.SelectedItem;

            if (discoveredDevice == null)
            {
                //rootPage.NotifyUser("No device selected, please select one.", NotifyType.ErrorMessage);
                return;
            }

            DeviceUnpairingResult result = await discoveredDevice.DeviceInfo.Pairing.UnpairAsync();
        }

        private async void btnFromId_Click(object sender, RoutedEventArgs e)
        {
            DiscoveredDevice discoveredDevice = (DiscoveredDevice)lvDiscoveredDevices.SelectedItem;

            if (discoveredDevice == null)
            {
                //rootPage.NotifyUser("No device selected, please select one.", NotifyType.ErrorMessage);
                return;
            }

            //rootPage.NotifyUser($"Connecting to {discoveredDevice.DeviceInfo.Name}...", NotifyType.StatusMessage);

            if (!discoveredDevice.DeviceInfo.Pairing.IsPaired)
            {
                if (!await connectionSettingsPanel.RequestPairDeviceAsync(discoveredDevice.DeviceInfo.Pairing))
                {
                    return;
                }
            }

            try
            {
                // IMPORTANT: FromIdAsync needs to be called from the UI thread
                WiFiDirectDevice wfdDevice = await WiFiDirectDevice.FromIdAsync(discoveredDevice.DeviceInfo.Id);

                // Register for the ConnectionStatusChanged event handler
                wfdDevice.ConnectionStatusChanged += OnConnectionStatusChanged;

                IReadOnlyList<EndpointPair> endpointPairs = wfdDevice.GetConnectionEndpointPairs();
                HostName remoteHostName = endpointPairs[0].RemoteHostName;

                //rootPage.NotifyUser($"Devices connected on L2 layer, connecting to IP Address: {remoteHostName} Port: {Globals.strServerPort}",
                //    NotifyType.StatusMessage);

                // Wait for server to start listening on a socket
                await Task.Delay(2000);

                // Connect to Advertiser on L4 layer
                StreamSocket clientSocket = new StreamSocket();
                await clientSocket.ConnectAsync(remoteHostName, Globals.strServerPort);
                //rootPage.NotifyUser("Connected with remote side on L4 layer", NotifyType.StatusMessage);

                SocketReaderWriter socketRW = new SocketReaderWriter(clientSocket, this);

                string sessionId = Path.GetRandomFileName();
                ConnectedDevice connectedDevice = new ConnectedDevice(sessionId, wfdDevice, socketRW);
                ConnectedDevices.Add(connectedDevice);

                // The first message sent over the socket is the name of the connection.
                await socketRW.WriteMessageAsync(sessionId);

                while (await socketRW.ReadMessageAsync() != null)
                {
                    // Keep reading messages
                }

            }
            catch (TaskCanceledException)
            {
                //rootPage.NotifyUser("FromIdAsync was canceled by user", NotifyType.ErrorMessage);
            }
            catch (Exception ex)
            {
                //rootPage.NotifyUser($"Connect operation threw an exception: {ex.Message}", NotifyType.ErrorMessage);
            }
        }

        private void OnConnectionStatusChanged(WiFiDirectDevice sender, object arg)
        {
            //rootPage.NotifyUserFromBackground($"Connection status changed: {sender.ConnectionStatus}", NotifyType.StatusMessage);
        }
        private void btnWatcher_Click(object sender, RoutedEventArgs e)
        {
            if (_fWatcherStarted == false)
            {

                btnStartAdvertisement_Click(sender, e);
                if (_publisher.Status != WiFiDirectAdvertisementPublisherStatus.Started)
                {
                    //rootPage.NotifyUser("Failed to start advertisement.", NotifyType.ErrorMessage);
                    return;
                }

                DiscoveredDevices.Clear();
                //rootPage.NotifyUser("Finding Devices...", NotifyType.StatusMessage);

                string deviceSelector = WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint);
                //    Utils.GetSelectedItemTag<WiFiDirectDeviceSelectorType>(cmbDeviceSelector));

                _deviceWatcher = DeviceInformation.CreateWatcher(deviceSelector, new string[] { "System.Devices.WiFiDirect.InformationElements" });

                _deviceWatcher.Added += OnDeviceAdded;
                _deviceWatcher.Removed += OnDeviceRemoved;
                _deviceWatcher.Updated += OnDeviceUpdated;
                _deviceWatcher.EnumerationCompleted += OnEnumerationCompleted;
                _deviceWatcher.Stopped += OnStopped;

                _deviceWatcher.Start();

                btnWatcher.Content = "Stop Watcher";
                _fWatcherStarted = true;
            }
            else
            {
                _publisher.Stop();

                btnWatcher.Content = "Start Watcher";
                _fWatcherStarted = false;

                _deviceWatcher.Added -= OnDeviceAdded;
                _deviceWatcher.Removed -= OnDeviceRemoved;
                _deviceWatcher.Updated -= OnDeviceUpdated;
                _deviceWatcher.EnumerationCompleted -= OnEnumerationCompleted;
                _deviceWatcher.Stopped -= OnStopped;

                _deviceWatcher.Stop();

                //rootPage.NotifyUser("Device watcher stopped.", NotifyType.StatusMessage);
            }
        }

        #region DeviceWatcherEvents
        private async void OnDeviceAdded(DeviceWatcher deviceWatcher, DeviceInformation deviceInfo)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                DiscoveredDevices.Add(new DiscoveredDevice(deviceInfo));
            });
        }

        private async void OnDeviceRemoved(DeviceWatcher deviceWatcher, DeviceInformationUpdate deviceInfoUpdate)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                foreach (DiscoveredDevice discoveredDevice in DiscoveredDevices)
                {
                    if (discoveredDevice.DeviceInfo.Id == deviceInfoUpdate.Id)
                    {
                        DiscoveredDevices.Remove(discoveredDevice);
                        break;
                    }
                }
            });
        }

        private async void OnDeviceUpdated(DeviceWatcher deviceWatcher, DeviceInformationUpdate deviceInfoUpdate)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                foreach (DiscoveredDevice discoveredDevice in DiscoveredDevices)
                {
                    if (discoveredDevice.DeviceInfo.Id == deviceInfoUpdate.Id)
                    {
                        discoveredDevice.UpdateDeviceInfo(deviceInfoUpdate);
                        break;
                    }
                }
            });
        }

        private void OnEnumerationCompleted(DeviceWatcher deviceWatcher, object o)
        {
            //rootPage.NotifyUserFromBackground("DeviceWatcher enumeration completed", NotifyType.StatusMessage);
        }

        private void OnStopped(DeviceWatcher deviceWatcher, object o)
        {
            //rootPage.NotifyUserFromBackground("DeviceWatcher stopped", NotifyType.StatusMessage);
        }
        #endregion

        private void btnIe_Click(object sender, RoutedEventArgs e)
        {
            WiFiDirectInformationElement informationElement = new WiFiDirectInformationElement();

            //// Information element blob
            //DataWriter dataWriter = new DataWriter();
            //dataWriter.UnicodeEncoding = UnicodeEncoding.Utf8;
            //dataWriter.ByteOrder = ByteOrder.LittleEndian;
            //dataWriter.WriteUInt32(dataWriter.MeasureString(txtInformationElement.Text));
            //dataWriter.WriteString(txtInformationElement.Text);
            //informationElement.Value = dataWriter.DetachBuffer();

            //// Organizational unit identifier (OUI)
            //informationElement.Oui = CryptographicBuffer.CreateFromByteArray(Globals.CustomOui);

            //// OUI Type
            //informationElement.OuiType = Globals.CustomOuiType;

            //// Save this information element so we can add it when we advertise.
            //_informationElements.Add(informationElement);

            //txtInformationElement.Text = "";
            //rootPage.NotifyUser("IE added successfully", NotifyType.StatusMessage);
        }
    }
}
