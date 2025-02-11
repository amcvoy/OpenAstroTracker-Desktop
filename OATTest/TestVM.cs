﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using OATCommunications;
using OATCommunications.Model;
using OATCommunications.Utilities;
using OATCommunications.WPF;
using OATCommunications.WPF.CommunicationHandlers;
using OATTest.Properties;

namespace OATTest
{
	public class TestVM : NotifyPropertyChanged
	{
		TestManager _testManager;
		ObservableCollection<string> _debugOutput;
		ICommunicationHandler _handler;
		SerialListener _debugHandler;
		AsyncAutoResetEvent _asyncAutoResetEvent = new AsyncAutoResetEvent();
		CultureInfo _oatCulture = new CultureInfo("en-US");
		string _commandPort;
		string _debugPort;
		bool _seperateDebugPort;
		DateTime _useDateTime;
		DateTime _presetTime;
		DelegateCommand _setDateTimePresetCommand;
		DelegateCommand _setDateTimeCommand;
		DelegateCommand _runTestsCommand;
		DelegateCommand _resetTestsCommand;
		DelegateCommand _resetScanDevicesCommand;
		private bool _failedToConnect;
		private string _debugBaudRate;
		private string _commandBaudRate;
		private List<string> _baudRates;
		private string _appStatus;
		private string _runButtonText = "Run";
		private TestSuite _selectedTestSuite;
		private int _succeeded;
		private int _failed;
		private int _skipped;
		private int _completed;
		private string _succeededTests;
		private string _failedTests;
		private string _skippedTests;
		private string _completedTests;

		public ICommand SetDateTimeToPresetCommand { get { return _setDateTimePresetCommand; } }
		public ICommand SetDateTimeToNowCommand { get { return _setDateTimeCommand; } }
		public ICommand RunTestsCommand { get { return _runTestsCommand; } }
		public ICommand ResetTestsCommand { get { return _resetTestsCommand; } }
		public ICommand ResetScanDevicesCommand { get { return _resetScanDevicesCommand; } }

		public TestVM()
		{
			_useDateTime = DateTime.Parse("03/28/22 23:00:00");
			_presetTime = _useDateTime;
			TestSuites = new ObservableCollection<TestSuite>();

			CommunicationHandlerFactory.Initialize();
			_testManager = new TestManager();
			_seperateDebugPort = false;
			_debugOutput = new ObservableCollection<string>();
			OnPropertyChanged("Tests");
			_debugOutput.Add("Welcome to TestManager V1.0");
			_setDateTimeCommand = new DelegateCommand(s => OnSetDateTime(true));
			_setDateTimePresetCommand = new DelegateCommand(s => OnSetDateTime(false));
			_runTestsCommand = new DelegateCommand(async (s) => await OnStartTests());
			_resetTestsCommand = new DelegateCommand(() => OnResetTests());
			_resetScanDevicesCommand = new DelegateCommand(() => OnRescanDevices());
			_commandBaudRate = "19200";
			_debugBaudRate = "57600";

			AvailableDevices = new ObservableCollection<string>();
			AvailableBaudRates = new List<string>() { "9600", "19200", "28800", "38400", "57600", "115200" };

			Task.Run(async () => { await OnDiscoverDevices(); });

			_commandPort = Settings.Default.COMPort ?? _commandPort;
			_commandBaudRate = string.IsNullOrEmpty(Settings.Default.COMBaud) ? _commandBaudRate : Settings.Default.COMBaud;
			_debugPort = Settings.Default.DebugPort ?? _debugPort;
			_debugBaudRate = string.IsNullOrEmpty(Settings.Default.DebugBaud) ? _debugBaudRate : Settings.Default.DebugBaud;

			foreach (var testSuite in _testManager.TestSuites)
			{
				TestSuites.Add(testSuite);
				if (testSuite.Name == Settings.Default.Suite)
				{
					SelectedTestSuite = testSuite;
				}
			}
		}

		void Debug(string line)
		{
			Log.WriteLine(line);
			WpfUtilities.RunOnUiThread(() => _debugOutput.Add(line), Application.Current.Dispatcher);
		}

		public void OnRescanDevices()
		{
			Task.Run(async () => { await OnDiscoverDevices(); });
		}

		public void OnResetTests()
		{
			_testManager.ResetAllTests();
			TestSuites.Clear();
			foreach (var testSuite in _testManager.TestSuites)
			{
				TestSuites.Add(testSuite);
				if (testSuite.Name == Settings.Default.Suite)
				{
					SelectedTestSuite = testSuite;
				}
			}

			_completed = 0;
			OnPropertyChanged("NumTests");
			AppStatus = string.Empty;
			SkippedTests = "-";
			SucceededTests = "-";
			FailedTests = "-";
			CompletedTests = "-";
			_debugOutput.Clear();
		}

		private async Task OnStartTests()
		{
			if (_testManager.AreTestsRunning)
			{
				AppStatus = "Aborting tests...";
				RunButtonText = "Waiting...";
				_testManager.AbortRun();
				_asyncAutoResetEvent.Set();
				return;
			}

			RunButtonText = "Abort";
			AppStatus = "Preparing tests...";
			_testManager.UseDate = _useDateTime;
			_testManager.ResetAllTests();
			_testManager.PrepareForRun();
			_succeeded = 0;
			_failed = 0;
			_skipped = 0;
			_completed = 0;
			_debugOutput.Clear();

			UpdateResults(CommandTest.StatusType.Ready);

			_failedToConnect = true;
			try
			{
				if (IsDebugPortSeperate)
				{
					string debugPort = DebugPort + "@" + DebugBaudRate;
					Debug($"Connecting Debug channel to OAT on {debugPort}...");
					AppStatus = "Connecting debug channel to OAT ...";
					_debugHandler = new SerialListener(debugPort, this.Debug);
					_debugHandler.Connect();
					await Task.Delay(500);
				}
				string port = CommandPort + "@" + _commandBaudRate;
				Debug($"Connecting to OAT on {port}...");
				AppStatus = "Connecting to OAT ...";
				_handler = CommunicationHandlerFactory.ConnectToDevice(port);
				_handler.Connect();
				await Task.Delay(3000);

				_handler.SendCommand(":GVN#", (resultNr) =>
				{
					Debug($"OAT Version command replied ...");
					if (resultNr.Success)
					{
						var versionNumbers = resultNr.Data.Substring(1).Split(".".ToCharArray());
						if (versionNumbers.Length != 3)
						{
							Debug($"Unrecognizable firmware version '{resultNr.Data}'");
						}
						else
						{
							try
							{
								FirmwareVersion = long.Parse(versionNumbers[0]) * 10000L + long.Parse(versionNumbers[1]) * 100L + long.Parse(versionNumbers[2]);
								_testManager.FirmwareVersion = FirmwareVersion;

								AppStatus = $"Connected to OAT {resultNr.Data}, running tests...";

								Debug($"OAT is running firmware version '{resultNr.Data}' => {FirmwareVersion}");
								_failedToConnect = false;
							}
							catch
							{
								Debug($"Unable to parse firmware version '{resultNr.Data}'");
							}
						}
					}

					Debug($"Signalling OAT Version is done...");
					_asyncAutoResetEvent.Set();
				});

				Debug($"Await OAT Version...");
				await _asyncAutoResetEvent.WaitAsync();
			}
			catch (Exception ex)
			{
				Debug("Failed to connect. " + ex.Message);
			}

			if (!_failedToConnect)
			{
				Debug($"Connected to OAT with V{FirmwareVersion}, running tests...");
				_testManager.PrepareForRun();
				await _testManager.RunAllTests(_handler, (result) => UpdateResults(result), (s) => Debug(s));

				Debug($"Tests complete, disconnecting...");
			}
			else
			{
				Debug($"Failed to connect to OAT, no tests run.");
			}

			AppStatus = $"Disconnecting...";

			await Task.Delay(500);
			if (_debugHandler != null && _debugHandler.Connected)
			{
				_debugHandler.Disconnect();
				_debugHandler = null;
			}
			await Task.Delay(250);

			if (_handler.Connected)
			{
				_handler.Disconnect();
				_handler = null;
			}
			await Task.Delay(250);

			Debug($"Finished.");

			AppStatus = $"Tests complete.";

			UpdateResults(CommandTest.StatusType.Ready);

			RunButtonText = "Run";
		}

		private void UpdateResults(CommandTest.StatusType result)
		{
			if (result == CommandTest.StatusType.Failed)
			{
				_failed++;
				_completed++;
			}
			if (result == CommandTest.StatusType.Skipped)
			{
				_skipped++;
				_completed++;
			}
			if (result == CommandTest.StatusType.Success)
			{
				_succeeded++;
				_completed++;
			}
			if (result == CommandTest.StatusType.Complete)
			{
				_succeeded++;
				_completed++;
			}

			CompletedTests = $"{_completed}/{_testManager.Tests.Count} completed";
			FailedTests = $"{_failed} failed";
			SkippedTests = $"{_skipped} skipped";
			SucceededTests = $"{_succeeded} succeeded";
		}

		private void OnSetDateTime(bool useNow)
		{
			_useDateTime = useNow ? DateTime.Now : _presetTime;
			OnPropertyChanged("UseTime");
			OnPropertyChanged("UseDate");
		}

		public ObservableCollection<string> AvailableDevices { get; private set; }
		public List<string> AvailableBaudRates { get { return _baudRates; } set { _baudRates = value; } }

		public bool CanRun
		{
			get
			{
				return !string.IsNullOrEmpty(CommandPort) && !string.IsNullOrEmpty(_commandBaudRate) && Tests.Any();
			}
		}

		public bool CanReset
		{
			get
			{
				return true;
			}
		}

		public async Task OnDiscoverDevices()
		{
			WpfUtilities.RunOnUiThread(() => { AvailableDevices.Clear(); }, Application.Current.Dispatcher);

			CommunicationHandlerFactory.DiscoverDevices();
			await Task.Delay(500);

			foreach (var device in CommunicationHandlerFactory.AvailableDevices)
			{
				//var handler = CommunicationHandlerFactory.AvailableHandlers.First(h => h.IsDriverForDevice(device));
				//var driver = new DeviceDriver(device, handler.SupportsSetupDialog, new DelegateCommand((p) => OnRunDeviceHandlerSetup(handler, p)));
				WpfUtilities.RunOnUiThread(() => { AvailableDevices.Add(device); }, Application.Current.Dispatcher);
			}
		}

		public ObservableCollection<TestSuite> TestSuites { get; private set; }
		public TestSuite SelectedTestSuite
		{
			get { return _selectedTestSuite; }
			set
			{
				if (_selectedTestSuite != value)
				{
					_selectedTestSuite = value;

					_testManager.SetActiveTestSuite(_selectedTestSuite?.Name ?? string.Empty);

					SaveSettings();
					OnPropertyChanged();
					OnPropertyChanged("CanRun");
					OnPropertyChanged("NumTests");
				}
			}
		}

		public IList<CommandTest> Tests
		{
			get { return _testManager.Tests; }
		}

		public ObservableCollection<string> DebugOutput
		{
			get
			{
				return _debugOutput;
			}
		}

		public string CommandPort
		{
			get { return _commandPort; }
			set
			{
				if (_commandPort != value)
				{
					_commandPort = value;
					if (!_seperateDebugPort)
					{
						DebugPort = value;
					}
					SaveSettings();
					OnPropertyChanged();
					OnPropertyChanged("CanRun");
				}
			}
		}

		private void SaveSettings()
		{
			Settings.Default.COMPort = CommandPort;
			Settings.Default.COMBaud = CommandBaudRate;
			Settings.Default.DebugPort = DebugPort;
			Settings.Default.DebugBaud = DebugBaudRate;
			Settings.Default.Suite = SelectedTestSuite?.Name ?? string.Empty;
			Settings.Default.Save();
		}

		public string DebugPort
		{
			get { return _debugPort; }
			set
			{
				if (_debugPort != value)
				{
					_debugPort = value;
					SaveSettings();
					OnPropertyChanged();
				}
			}
		}

		public bool IsDebugPortSeperate
		{
			get { return _seperateDebugPort; }
			set
			{
				if (_seperateDebugPort != value)
				{
					_seperateDebugPort = value;
					if (string.IsNullOrEmpty(DebugPort))
					{
						DebugPort = CommandPort;
					}
					OnPropertyChanged();
				}
			}
		}

		public string UseDate
		{
			get { return _useDateTime.ToString("MM/dd/yy"); }
			set
			{
				DateTime date;
				if (DateTime.TryParse(value, out date))
				{
					_useDateTime = new DateTime(date.Year, date.Month, date.Day, _useDateTime.Hour, _useDateTime.Minute, _useDateTime.Second);
					OnPropertyChanged();
				}
			}
		}

		public string UseTime
		{
			get { return _useDateTime.ToString("HH:mm:ss"); }
			set
			{
				DateTime time;
				if (DateTime.TryParse(value, out time))
				{
					_useDateTime = new DateTime(_useDateTime.Year, _useDateTime.Month, _useDateTime.Day, time.Hour, time.Minute, time.Second);
					OnPropertyChanged();
				}
			}
		}

		public string RunButtonText
		{
			get { return _runButtonText; }
			set
			{
				if (_runButtonText != value)
				{
					_runButtonText = value;
					OnPropertyChanged();
				}
			}
		}
		public string DebugBaudRate
		{
			get { return _debugBaudRate; }
			set
			{
				if (_debugBaudRate != value)
				{
					_debugBaudRate = value;
					OnPropertyChanged();
					SaveSettings();
				}
			}
		}

		public string CommandBaudRate
		{
			get { return _commandBaudRate; }
			set
			{
				if (_commandBaudRate != value)
				{
					_commandBaudRate = value;
					OnPropertyChanged();
					OnPropertyChanged("CanRun");
					SaveSettings();
				}
			}
		}

		public string AppStatus
		{
			get { return _appStatus; }
			set
			{
				if (_appStatus != value)
				{
					_appStatus = value;
					OnPropertyChanged();
					OnPropertyChanged("NumTests");
				}
			}
		}

		public string SucceededTests
		{
			get { return _succeededTests; }
			set
			{
				if (_succeededTests != value)
				{
					_succeededTests = value;
					OnPropertyChanged();
				}
			}
		}

		public string FailedTests
		{
			get { return _failedTests; }
			set
			{
				if (_failedTests != value)
				{
					_failedTests = value;
					OnPropertyChanged();
				}
			}
		}

		public string SkippedTests
		{
			get { return _skippedTests; }
			set
			{
				if (_skippedTests != value)
				{
					_skippedTests = value;
					OnPropertyChanged();
				}
			}
		}

		public string CompletedTests
		{
			get { return _completedTests; }
			set
			{
				if (_completedTests != value)
				{
					_completedTests = value;
					OnPropertyChanged();
					OnPropertyChanged("TestsCompleted");
				}
			}
		}

		public long TestsCompleted
		{
			get { return _completed; }
		}

		public long NumTests
		{
			get { return Tests.Count; }
		}



		public long FirmwareVersion { get; private set; }
	}
}

