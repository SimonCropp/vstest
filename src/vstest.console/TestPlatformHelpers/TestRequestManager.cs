// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.XPath;

using Microsoft.VisualStudio.TestPlatform.Client;
using Microsoft.VisualStudio.TestPlatform.Client.RequestHelper;
using Microsoft.VisualStudio.TestPlatform.CommandLine.Internal;
using Microsoft.VisualStudio.TestPlatform.CommandLine.Processors.Utilities;
using Microsoft.VisualStudio.TestPlatform.CommandLine.Publisher;
using Microsoft.VisualStudio.TestPlatform.CommandLineUtilities;
using Microsoft.VisualStudio.TestPlatform.Common;
using Microsoft.VisualStudio.TestPlatform.Common.Interfaces;
using Microsoft.VisualStudio.TestPlatform.Common.Telemetry;
using Microsoft.VisualStudio.TestPlatform.Common.Utilities;
using Microsoft.VisualStudio.TestPlatform.CoreUtilities.Tracing;
using Microsoft.VisualStudio.TestPlatform.CoreUtilities.Tracing.Interfaces;
using Microsoft.VisualStudio.TestPlatform.CrossPlatEngine;
using Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.TestRunAttachmentsProcessing;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.Interfaces;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.Payloads;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Engine;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Utilities;
using Microsoft.VisualStudio.TestPlatform.PlatformAbstractions;
using Microsoft.VisualStudio.TestPlatform.PlatformAbstractions.Interfaces;
using Microsoft.VisualStudio.TestPlatform.Utilities;
using Microsoft.VisualStudio.TestPlatform.Utilities.Helpers;

namespace Microsoft.VisualStudio.TestPlatform.CommandLine.TestPlatformHelpers;

/// <summary>
/// Defines the test request manger which can fire off discovery and test run requests.
/// </summary>
internal class TestRequestManager : ITestRequestManager
{
    private static ITestRequestManager? s_testRequestManagerInstance;

    // Defines the default architecture to be used for AnyCPU or non-dll sources. This is just temporary, and unsupported, DO NOT use.
    private static readonly string VSTEST_DEFAULT_ARCHITECTURE_FOR_ANYCPU = nameof(VSTEST_DEFAULT_ARCHITECTURE_FOR_ANYCPU);

    private readonly ITestPlatform _testPlatform;
    private readonly ITestPlatformEventSource _testPlatformEventSource;
    // TODO: No idea what is Task supposed to buy us, Tasks start immediately on instantiation
    // and the work done to produce the metrics publisher is minimal.
    private readonly Task<IMetricsPublisher> _metricsPublisher;
    private readonly object _syncObject = new();

    private bool _isDisposed;
    private bool _telemetryOptedIn;
    private readonly CommandLineOptions _commandLineOptions;
    private readonly TestRunResultAggregator _testRunResultAggregator;
    private readonly InferHelper _inferHelper;
    private readonly IProcessHelper _processHelper;
    private readonly ITestRunAttachmentsProcessingManager _attachmentsProcessingManager;
    private readonly IEnvironment _environment;

    /// <summary>
    /// Maintains the current active execution request.
    /// Assumption: There can only be one active execution request.
    /// </summary>
    private ITestRunRequest? _currentTestRunRequest;

    /// <summary>
    /// Maintains the current active discovery request.
    /// Assumption: There can only be one active discovery request.
    /// </summary>
    private IDiscoveryRequest? _currentDiscoveryRequest;

    /// <summary>
    /// Maintains the current active test run attachments processing cancellation token source.
    /// Assumption: There can only be one active attachments processing request.
    /// </summary>
    private CancellationTokenSource? _currentAttachmentsProcessingCancellationTokenSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="TestRequestManager"/> class.
    /// </summary>
    public TestRequestManager()
        : this(
            CommandLineOptions.Instance,
            TestPlatformFactory.GetTestPlatform(),
            TestRunResultAggregator.Instance,
            TestPlatformEventSource.Instance,
            new InferHelper(AssemblyMetadataProvider.Instance),
            MetricsPublisherFactory.GetMetricsPublisher(
                IsTelemetryOptedIn(),
                CommandLineOptions.Instance.IsDesignMode),
            new ProcessHelper(),
            new TestRunAttachmentsProcessingManager(TestPlatformEventSource.Instance, new DataCollectorAttachmentsProcessorsFactory()),
            new PlatformEnvironment())
    {
    }

    internal TestRequestManager(
        CommandLineOptions commandLineOptions,
        ITestPlatform testPlatform,
        TestRunResultAggregator testRunResultAggregator,
        ITestPlatformEventSource testPlatformEventSource,
        InferHelper inferHelper,
        Task<IMetricsPublisher> metricsPublisher,
        IProcessHelper processHelper,
        ITestRunAttachmentsProcessingManager attachmentsProcessingManager,
        IEnvironment environment)
    {
        _testPlatform = testPlatform;
        _commandLineOptions = commandLineOptions;
        _testRunResultAggregator = testRunResultAggregator;
        _testPlatformEventSource = testPlatformEventSource;
        _inferHelper = inferHelper;
        _metricsPublisher = metricsPublisher;
        _processHelper = processHelper;
        _attachmentsProcessingManager = attachmentsProcessingManager;
        _environment = environment;
    }

    /// <summary>
    /// Gets the test request manager instance.
    /// </summary>
    public static ITestRequestManager Instance
        => s_testRequestManagerInstance ??= new TestRequestManager();

    #region ITestRequestManager

    /// <inheritdoc />
    public void InitializeExtensions(
        IEnumerable<string> pathToAdditionalExtensions,
        bool skipExtensionFilters)
    {
        // It is possible for an Editor/IDE to keep running the runner in design mode for long
        // duration. We clear the extensions cache to ensure the extensions don't get reused
        // across discovery/run requests.
        EqtTrace.Info("TestRequestManager.InitializeExtensions: Initialize extensions started.");
        _testPlatform.ClearExtensions();
        _testPlatform.UpdateExtensions(pathToAdditionalExtensions, skipExtensionFilters);
        EqtTrace.Info("TestRequestManager.InitializeExtensions: Initialize extensions completed.");
    }

    /// <inheritdoc />
    public void ResetOptions()
    {
        _commandLineOptions.Reset();
    }

    /// <inheritdoc />
    public void DiscoverTests(
        DiscoveryRequestPayload discoveryPayload,
        ITestDiscoveryEventsRegistrar discoveryEventsRegistrar,
        ProtocolConfig protocolConfig)
    {
        EqtTrace.Info("TestRequestManager.DiscoverTests: Discovery tests started.");

        // TODO: Normalize rest of the data on the request as well
        discoveryPayload.Sources = discoveryPayload.Sources?.Distinct().ToList() ?? new List<string>();
        discoveryPayload.RunSettings ??= "<RunSettings></RunSettings>";

        var runsettings = discoveryPayload.RunSettings;

        if (discoveryPayload.TestPlatformOptions != null)
        {
            _telemetryOptedIn = discoveryPayload.TestPlatformOptions.CollectMetrics;
        }

        var requestData = GetRequestData(protocolConfig);
        if (UpdateRunSettingsIfRequired(
                runsettings,
                discoveryPayload.Sources.ToList(),
                discoveryEventsRegistrar,
                out string updatedRunsettings,
                out IDictionary<string, Architecture> sourceToArchitectureMap,
                out IDictionary<string, Framework> sourceToFrameworkMap))
        {
            runsettings = updatedRunsettings;
        }

        var sourceToSourceDetailMap = discoveryPayload.Sources.Select(source => new SourceDetail
        {
            Source = source,
            Architecture = sourceToArchitectureMap[source],
            Framework = sourceToFrameworkMap[source],
        }).ToDictionary(k => k.Source);

        var runConfiguration = XmlRunSettingsUtilities.GetRunConfigurationNode(runsettings);
        var batchSize = runConfiguration.BatchSize;
        var testCaseFilterFromRunsettings = runConfiguration.TestCaseFilter;

        if (requestData.IsTelemetryOptedIn)
        {
            // Collect metrics.
            CollectMetrics(requestData, runConfiguration);

            // Collect commands.
            LogCommandsTelemetryPoints(requestData);
        }

        // Create discovery request.
        var criteria = new DiscoveryCriteria(
            discoveryPayload.Sources,
            batchSize,
            _commandLineOptions.TestStatsEventTimeout,
            runsettings,
            discoveryPayload.TestSessionInfo)
        {
            TestCaseFilter = _commandLineOptions.TestCaseFilterValue
                             ?? testCaseFilterFromRunsettings
        };

        // Make sure to run the run request inside a lock as the below section is not thread-safe.
        // There can be only one discovery or execution request at a given point in time.
        lock (_syncObject)
        {
            try
            {
                EqtTrace.Info("TestRequestManager.DiscoverTests: Synchronization context taken");

                _currentDiscoveryRequest = _testPlatform.CreateDiscoveryRequest(
                    requestData,
                    criteria,
                    discoveryPayload.TestPlatformOptions,
                    sourceToSourceDetailMap);
                discoveryEventsRegistrar?.RegisterDiscoveryEvents(_currentDiscoveryRequest);

                // Notify start of discovery start.
                _testPlatformEventSource.DiscoveryRequestStart();

                // Start the discovery of tests and wait for completion.
                _currentDiscoveryRequest.DiscoverAsync();
                _currentDiscoveryRequest.WaitForCompletion();
            }
            finally
            {
                if (_currentDiscoveryRequest != null)
                {
                    // Dispose the discovery request and unregister for events.
                    discoveryEventsRegistrar?.UnregisterDiscoveryEvents(_currentDiscoveryRequest);
                    _currentDiscoveryRequest.Dispose();
                    _currentDiscoveryRequest = null;
                }

                EqtTrace.Info("TestRequestManager.DiscoverTests: Discovery tests completed.");
                _testPlatformEventSource.DiscoveryRequestStop();

                // Posts the discovery complete event.
                _metricsPublisher.Result.PublishMetrics(
                    TelemetryDataConstants.TestDiscoveryCompleteEvent,
                    requestData.MetricsCollection.Metrics);
            }
        }
    }

    /// <inheritdoc />
    public void RunTests(
        TestRunRequestPayload testRunRequestPayload,
        ITestHostLauncher3? testHostLauncher,
        ITestRunEventsRegistrar testRunEventsRegistrar,
        ProtocolConfig protocolConfig)
    {
        EqtTrace.Info("TestRequestManager.RunTests: run tests started.");
        var runsettings = testRunRequestPayload.RunSettings;

        if (testRunRequestPayload.TestPlatformOptions != null)
        {
            _telemetryOptedIn = testRunRequestPayload.TestPlatformOptions.CollectMetrics;
        }

        var requestData = GetRequestData(protocolConfig);

        // Get sources to auto detect fx and arch for both run selected or run all scenario.
        var sources = GetSources(testRunRequestPayload);

        if (UpdateRunSettingsIfRequired(
                runsettings,
                sources,
                testRunEventsRegistrar,
                out string updatedRunsettings,
                out IDictionary<string, Architecture> sourceToArchitectureMap,
                out IDictionary<string, Framework> sourceToFrameworkMap))
        {
            runsettings = updatedRunsettings;
        }

        var sourceToSourceDetailMap = sources.Select(source => new SourceDetail
        {
            Source = source,
            Architecture = sourceToArchitectureMap[source!],
            Framework = sourceToFrameworkMap[source!],
        }).ToDictionary(k => k.Source);

        if (InferRunSettingsHelper.AreRunSettingsCollectorsIncompatibleWithTestSettings(runsettings))
        {
            throw new SettingsException(
                string.Format(
                    Resources.Resources.RunsettingsWithDCErrorMessage,
                    runsettings));
        }

        var runConfiguration = XmlRunSettingsUtilities.GetRunConfigurationNode(runsettings);
        var batchSize = runConfiguration.BatchSize;

        if (requestData.IsTelemetryOptedIn)
        {
            // Collect metrics.
            CollectMetrics(requestData, runConfiguration);

            // Collect commands.
            LogCommandsTelemetryPoints(requestData);

            // Collect data for legacy settings.
            LogTelemetryForLegacySettings(requestData, runsettings);
        }

        // Get Fakes data collector settings.
        if (!string.Equals(Environment.GetEnvironmentVariable("VSTEST_SKIP_FAKES_CONFIGURATION"), "1"))
        {
            // TODO: Are the sources in _commandLineOptions any different from the ones we get on the request?
            // because why would they be? We never pass that forward to the executor, so this probably should
            // just look at sources anyway.

            // The commandline options do not have sources in design time mode,
            // and so we fall back to using sources instead.
            if (_commandLineOptions.Sources.Any())
            {
                GenerateFakesUtilities.GenerateFakesSettings(
                    _commandLineOptions,
                    _commandLineOptions.Sources.ToList(),
                    ref runsettings);
            }
            else if (sources.Any())
            {
                GenerateFakesUtilities.GenerateFakesSettings(
                    _commandLineOptions,
                    sources,
                    ref runsettings);
            }
        }

        // We can have either a run that contains string as test container (usually a DLL), which is later resolved to the actual path
        // and all tests that match filter are run from that container.
        //
        // OR we already did discovery and have a list of TestCases that have concrete test method information
        // and so we only pass those. TestCase also has the test container path (usually a DLL).
        TestRunCriteria runCriteria = testRunRequestPayload.Sources != null && testRunRequestPayload.Sources.Any()
            ? new TestRunCriteria(
                testRunRequestPayload.Sources,
                batchSize,
                testRunRequestPayload.KeepAlive,
                runsettings,
                _commandLineOptions.TestStatsEventTimeout,
                testHostLauncher,
                testRunRequestPayload.TestPlatformOptions?.TestCaseFilter,
                testRunRequestPayload.TestPlatformOptions?.FilterOptions,
                testRunRequestPayload.TestSessionInfo,
                debugEnabledForTestSession: testRunRequestPayload.TestSessionInfo != null
                                            && testRunRequestPayload.DebuggingEnabled)
            : new TestRunCriteria(
                testRunRequestPayload.TestCases,
                batchSize,
                testRunRequestPayload.KeepAlive,
                runsettings,
                _commandLineOptions.TestStatsEventTimeout,
                testHostLauncher,
                testRunRequestPayload.TestSessionInfo,
                debugEnabledForTestSession: testRunRequestPayload.TestSessionInfo != null
                                            && testRunRequestPayload.DebuggingEnabled);

        // Run tests.
        try
        {
            RunTests(
                requestData,
                runCriteria,
                testRunEventsRegistrar,
                testRunRequestPayload.TestPlatformOptions,
                sourceToSourceDetailMap);
            EqtTrace.Info("TestRequestManager.RunTests: run tests completed.");
        }
        finally
        {
            _testPlatformEventSource.ExecutionRequestStop();

            // Post the run complete event
            _metricsPublisher.Result.PublishMetrics(
                TelemetryDataConstants.TestExecutionCompleteEvent,
                requestData.MetricsCollection.Metrics);
        }
    }

    /// <inheritdoc/>
    public void ProcessTestRunAttachments(
        TestRunAttachmentsProcessingPayload attachmentsProcessingPayload,
        ITestRunAttachmentsProcessingEventsHandler attachmentsProcessingEventsHandler,
        ProtocolConfig protocolConfig)
    {
        EqtTrace.Info("TestRequestManager.ProcessTestRunAttachments: Test run attachments processing started.");

        _telemetryOptedIn = attachmentsProcessingPayload.CollectMetrics;
        var requestData = GetRequestData(protocolConfig);

        // Make sure to run the run request inside a lock as the below section is not thread-safe.
        // There can be only one discovery, execution or attachments processing request at a given
        // point in time.
        lock (_syncObject)
        {
            try
            {
                EqtTrace.Info("TestRequestManager.ProcessTestRunAttachments: Synchronization context taken.");
                _testPlatformEventSource.TestRunAttachmentsProcessingRequestStart();

                _currentAttachmentsProcessingCancellationTokenSource = new CancellationTokenSource();

                Task task = _attachmentsProcessingManager.ProcessTestRunAttachmentsAsync(
                    attachmentsProcessingPayload.RunSettings,
                    requestData,
                    attachmentsProcessingPayload.Attachments,
                    attachmentsProcessingPayload.InvokedDataCollectors,
                    attachmentsProcessingEventsHandler,
                    _currentAttachmentsProcessingCancellationTokenSource.Token);
                task.Wait();
            }
            finally
            {
                if (_currentAttachmentsProcessingCancellationTokenSource != null)
                {
                    _currentAttachmentsProcessingCancellationTokenSource.Dispose();
                    _currentAttachmentsProcessingCancellationTokenSource = null;
                }

                EqtTrace.Info("TestRequestManager.ProcessTestRunAttachments: Test run attachments processing completed.");
                _testPlatformEventSource.TestRunAttachmentsProcessingRequestStop();

                // Post the attachments processing complete event.
                _metricsPublisher.Result.PublishMetrics(
                    TelemetryDataConstants.TestAttachmentsProcessingCompleteEvent,
                    requestData.MetricsCollection.Metrics);
            }
        }
    }

    /// <inheritdoc/>
    public void StartTestSession(
        StartTestSessionPayload payload,
        ITestHostLauncher3? testHostLauncher,
        ITestSessionEventsHandler eventsHandler,
        ProtocolConfig protocolConfig)
    {
        EqtTrace.Info("TestRequestManager.StartTestSession: Starting test session.");

        if (payload.TestPlatformOptions != null)
        {
            _telemetryOptedIn = payload.TestPlatformOptions.CollectMetrics;
        }

        payload.Sources ??= new List<string>();
        payload.RunSettings ??= "<RunSettings></RunSettings>";

        if (UpdateRunSettingsIfRequired(
                payload.RunSettings,
                payload.Sources,
                null,
                out string updatedRunsettings,
                out IDictionary<string, Architecture> sourceToArchitectureMap,
                out IDictionary<string, Framework> sourceToFrameworkMap))
        {
            payload.RunSettings = updatedRunsettings;
        }

        var sourceToSourceDetailMap = payload.Sources.Select(source => new SourceDetail
        {
            Source = source,
            Architecture = sourceToArchitectureMap[source],
            Framework = sourceToFrameworkMap[source],
        }).ToDictionary(k => k.Source);

        if (InferRunSettingsHelper.AreRunSettingsCollectorsIncompatibleWithTestSettings(payload.RunSettings))
        {
            throw new SettingsException(
                string.Format(
                    Resources.Resources.RunsettingsWithDCErrorMessage,
                    payload.RunSettings));
        }

        var runConfiguration = XmlRunSettingsUtilities.GetRunConfigurationNode(payload.RunSettings);
        var requestData = GetRequestData(protocolConfig);

        // Collect metrics & commands.
        CollectMetrics(requestData, runConfiguration);
        LogCommandsTelemetryPoints(requestData);

        lock (_syncObject)
        {
            try
            {
                EqtTrace.Info("TestRequestManager.StartTestSession: Synchronization context taken.");
                _testPlatformEventSource.StartTestSessionStart();

                var criteria = new StartTestSessionCriteria()
                {
                    Sources = payload.Sources,
                    RunSettings = payload.RunSettings,
                    TestHostLauncher = testHostLauncher
                };

                var testSessionStarted = _testPlatform.StartTestSession(requestData, criteria, eventsHandler, sourceToSourceDetailMap);
                if (!testSessionStarted)
                {
                    EqtTrace.Warning("TestRequestManager.StartTestSession: Unable to start test session.");
                }
            }
            finally
            {
                EqtTrace.Info("TestRequestManager.StartTestSession: Starting test session completed.");
                _testPlatformEventSource.StartTestSessionStop();

                // Post the attachments processing complete event.
                _metricsPublisher.Result.PublishMetrics(
                    TelemetryDataConstants.StartTestSessionCompleteEvent,
                    requestData.MetricsCollection.Metrics);
            }
        }
    }

    /// <inheritdoc/>
    public void StopTestSession(
        StopTestSessionPayload payload,
        ITestSessionEventsHandler eventsHandler,
        ProtocolConfig protocolConfig)
    {
        EqtTrace.Info("TestRequestManager.StopTestSession: Stopping test session.");

        _telemetryOptedIn = payload.CollectMetrics;
        var requestData = GetRequestData(protocolConfig);

        lock (_syncObject)
        {
            try
            {
                EqtTrace.Info("TestRequestManager.StopTestSession: Synchronization context taken.");
                _testPlatformEventSource.StopTestSessionStart();

                var stopped = TestSessionPool.Instance.KillSession(payload.TestSessionInfo, requestData);
                eventsHandler.HandleStopTestSessionComplete(
                    new()
                    {
                        TestSessionInfo = payload.TestSessionInfo,
                        Metrics = stopped ? requestData.MetricsCollection.Metrics : null,
                        IsStopped = stopped
                    });

                if (!stopped)
                {
                    EqtTrace.Warning("TestRequestManager.StopTestSession: Unable to stop test session.");
                }
            }
            finally
            {
                EqtTrace.Info("TestRequestManager.StopTestSession: Stopping test session completed.");
                _testPlatformEventSource.StopTestSessionStop();

                // Post the attachments processing complete event.
                _metricsPublisher.Result.PublishMetrics(
                    TelemetryDataConstants.StopTestSessionCompleteEvent,
                    requestData.MetricsCollection.Metrics);
            }
        }
    }

    private void LogTelemetryForLegacySettings(IRequestData requestData, string runsettings)
    {
        requestData.MetricsCollection.Add(
            TelemetryDataConstants.TestSettingsUsed,
            InferRunSettingsHelper.IsTestSettingsEnabled(runsettings));

        if (InferRunSettingsHelper.TryGetLegacySettingElements(
                runsettings,
                out Dictionary<string, string> legacySettingsTelemetry))
        {
            foreach (var ciData in legacySettingsTelemetry)
            {
                // We are collecting telemetry for the legacy nodes and attributes used in the runsettings.
                requestData.MetricsCollection.Add(
                    string.Format(
                        "{0}.{1}",
                        TelemetryDataConstants.LegacySettingPrefix,
                        ciData.Key),
                    ciData.Value);
            }
        }
    }

    /// <inheritdoc />
    public void CancelTestRun()
    {
        EqtTrace.Info("TestRequestManager.CancelTestRun: Sending cancel request.");
        _currentTestRunRequest?.CancelAsync();
    }

    /// <inheritdoc />
    public void CancelDiscovery()
    {
        EqtTrace.Info("TestRequestManager.CancelDiscovery: Sending cancel request.");
        _currentDiscoveryRequest?.Abort();
    }

    /// <inheritdoc />
    public void AbortTestRun()
    {
        EqtTrace.Info("TestRequestManager.AbortTestRun: Sending abort request.");
        _currentTestRunRequest?.Abort();
    }

    /// <inheritdoc/>
    public void CancelTestRunAttachmentsProcessing()
    {
        EqtTrace.Info("TestRequestManager.CancelTestRunAttachmentsProcessing: Sending cancel request.");
        _currentAttachmentsProcessingCancellationTokenSource?.Cancel();
    }

    #endregion

    public void Dispose()
    {
        Dispose(true);

        // Use SupressFinalize in case a subclass
        // of this type implements a finalizer.
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                _metricsPublisher.Result.Dispose();
            }

            _isDisposed = true;
        }
    }

    private bool UpdateRunSettingsIfRequired(
        string runsettingsXml,
        IList<string?>? sources,
        IBaseTestEventsRegistrar? registrar,
        out string updatedRunSettingsXml,
        out IDictionary<string, Architecture> sourceToArchitectureMap,
        out IDictionary<string, Framework> sourceToFrameworkMap)
    {
        bool settingsUpdated = false;
        updatedRunSettingsXml = runsettingsXml ?? throw new ArgumentNullException(nameof(runsettingsXml));

        // TargetFramework is full CLR. Set DesignMode based on current context.
        using var stream = new StringReader(runsettingsXml);
        using var reader = XmlReader.Create(
            stream,
            XmlRunSettingsUtilities.ReaderSettings);
        var document = new XmlDocument();
        document.Load(reader);
        var navigator = document.CreateNavigator();
        var runConfiguration = XmlRunSettingsUtilities.GetRunConfigurationNode(runsettingsXml);
        var loggerRunSettings = XmlRunSettingsUtilities.GetLoggerRunSettings(runsettingsXml)
                                ?? new LoggerRunSettings();


        // True when runsettings don't set target framework. False when runsettings force target framework
        // in both cases the sourceToFrameworkMap is populated with the real frameworks as we inferred them
        // from dlls. For sources like .js, we return the default framework.
        var frameworkWasAutodetected = UpdateFrameworkInRunSettingsIfRequired(
            document,
            navigator,
            sources,
            registrar,
            out Framework chosenFramework,
            out sourceToFrameworkMap);

        settingsUpdated |= frameworkWasAutodetected;
        var frameworkSetByRunsettings = !frameworkWasAutodetected;

        // Before MULTI_TFM feature the sourceToArchitectureMap and sourceToFrameworkMap were only used as informational
        // to be able to do this compatibility check and print warning. And in the later steps only chosenPlatform, chosenFramework
        // were used, that represented the single architecture and framework to be used.
        //
        // After MULTI_TFM  sourceToArchitectureMap and sourceToFrameworkMap are the source of truth, and are propagated forward,
        // so when we want to revert to the older behavior we need to re-enable the check, and unify all the architecture and
        // framework entries to the same chosen value.
        var disableMultiTfm = FeatureFlag.Instance.IsSet(FeatureFlag.DISABLE_MULTI_TFM_RUN);

        // Choose default architecture based on the framework.
        // For a run with mixed tfms enabled, or .NET "Core", the default platform architecture should be based on the process.
        // This will choose x64 by default for both .NET and .NET Framework, and avoids choosing x86 for a mixed
        // run, so we will run via .NET testhost.exe, and not via dotnet testhost.dll.
        Architecture defaultArchitecture = Architecture.X86;
        if (!disableMultiTfm
            || chosenFramework.Name.IndexOf("netstandard", StringComparison.OrdinalIgnoreCase) >= 0
            || chosenFramework.Name.IndexOf("netcoreapp", StringComparison.OrdinalIgnoreCase) >= 0
            // This is a special case for 1 version of Nuget.Frameworks that was shipped with using identifier NET5 instead of NETCoreApp5 for .NET 5.
            || chosenFramework.Name.IndexOf("net5", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            // We are running in vstest.console that is either started via dotnet
            // or via vstest.console.exe. The architecture of the current process
            // determines the default architecture to use for AnyCPU dlls
            // and other sources that don't dictate architecture (e.g. js files).
            // This way starting 32-bit dotnet will try to run as 32-bit testhost
            // using the runtime that was installed with that 32-bit dotnet SDK.
            // Similarly ARM64 vstest.console will start ARM64 testhost, making sure
            // that we choose the architecture that we already know we can run as.
            // 64-bit SDK when running from 64-bit dotnet process.
            // As default architecture we specify the expected test host architecture,
            // it can be specified by user on the command line with --arch or through runsettings.
            // If it's not specified by user will be filled by current processor architecture;
            // should be the same as SDK.
            defaultArchitecture = GetDefaultArchitecture(runConfiguration, runsettingsXml);
        }
        else
        {
            if (_environment.Architecture == PlatformArchitecture.ARM64 && _environment.OperatingSystem == PlatformOperatingSystem.Windows)
            {
                // For non .NET Core containers only on win ARM64 we want to run AnyCPU using current process architecture as a default
                // for both vstest.console.exe and design mode scenario.
                // As default architecture we specify the expected test host architecture,
                // it can be specified by user on the command line with /Platform or through runsettings.
                // If it's not specified by user will be filled by current processor architecture.
                defaultArchitecture = GetDefaultArchitecture(runConfiguration, runsettingsXml);
            }

            // Other scenarios, most notably .NET Framework with MultiTFM disabled, will use the old default X86 architecture.
        }

        EqtTrace.Verbose($"TestRequestManager.UpdateRunSettingsIfRequired: Default architecture: {defaultArchitecture} IsDefaultTargetArchitecture: {RunSettingsHelper.Instance.IsDefaultTargetArchitecture}, Current process architecture: {_processHelper.GetCurrentProcessArchitecture()} OperatingSystem: {_environment.OperatingSystem}.");

        // True when runsettings don't set platformk. False when runsettings force platform
        // in both cases the sourceToArchitectureMap is populated with the real architecture as we inferred it
        // from dlls. For sources like .js, we return the default architecture.
        var platformWasAutodetected = UpdatePlatform(
            document,
            navigator,
            sources,
            defaultArchitecture,
            out Architecture chosenPlatform,
            out sourceToArchitectureMap);

        settingsUpdated |= platformWasAutodetected;
        var platformSetByRunsettings = !platformWasAutodetected;

        // Before MULTI_TFM feature the sourceToArchitectureMap and sourceToFrameworkMap were only used as informational
        // to be able to do this compatibility check and print warning. And in the later steps only chosenPlatform, chosenFramework
        // were used, that represented the single architecture and framework to be used.
        //
        // After MULTI_TFM  sourceToArchitectureMap and sourceToFrameworkMap are the source of truth, and are propagated forward,
        // so when we want to revert to the older behavior we need to re-enable the check, and unify all the architecture and
        // framework entries to the same chosen value.

        // Do the check only when we enable MULTI_TFM and platform or framework are forced by settings, because then we maybe have some sources
        // that are not compatible with the chosen settings. And do the check always when MULTI_TFM is disabled, because then we want to warn every
        // time there are multiple tfms or architectures mixed.
        if (disableMultiTfm || (!disableMultiTfm && (platformSetByRunsettings || frameworkSetByRunsettings)))
        {
            CheckSourcesForCompatibility(
                chosenFramework,
                chosenPlatform,
                defaultArchitecture,
                sourceToArchitectureMap,
                sourceToFrameworkMap,
                registrar);
        }

        // The sourceToArchitectureMap contains the real architecture, overwrite it by the value chosen by runsettings, to force one unified platform to be used.
        if (disableMultiTfm || platformSetByRunsettings)
        {
            // Copy the list of key, otherwise we will get collection changed exception.
            var keys = sourceToArchitectureMap.Keys.ToList();
            foreach (var key in keys)
            {
                sourceToArchitectureMap[key] = chosenPlatform;
            }
        }

        // The sourceToFrameworkMap contains the real framework, overwrite it by the value chosen by runsettings, to force one unified framework to be used.
        if (disableMultiTfm || frameworkSetByRunsettings)
        {
            // Copy the list of key, otherwise we will get collection changed exception.
            var keys = sourceToFrameworkMap.Keys.ToList();
            foreach (var key in keys)
            {
                sourceToFrameworkMap[key] = chosenFramework;
            }
        }

        settingsUpdated |= UpdateDesignMode(document, runConfiguration);
        settingsUpdated |= UpdateCollectSourceInformation(document, runConfiguration);
        settingsUpdated |= UpdateTargetDevice(navigator, document);
        settingsUpdated |= AddOrUpdateConsoleLogger(document, runConfiguration, loggerRunSettings);

        updatedRunSettingsXml = navigator.OuterXml;

        return settingsUpdated;

        Architecture GetDefaultArchitecture(RunConfiguration runConfiguration, string runsettingsXml)
        {
            if (!RunSettingsHelper.Instance.IsDefaultTargetArchitecture)
            {
                return runConfiguration.TargetPlatform;
            }

            // Returns null, where there are none.
            Dictionary<string, string?>? environmentVariables = InferRunSettingsHelper.GetEnvironmentVariables(runsettingsXml);
            if (environmentVariables != null)
            {
                string? defaultArchitectureFromRunsettings = environmentVariables.TryGetValue(VSTEST_DEFAULT_ARCHITECTURE_FOR_ANYCPU, out var architecture) ? architecture : null;

                if (defaultArchitectureFromRunsettings != null)
                {
                    Architecture? defaultArchitecture = Enum.TryParse<Architecture>(defaultArchitectureFromRunsettings, out var arch) ? arch : null;

                    if (defaultArchitecture != null)
                    {
                        return defaultArchitecture.Value;
                    }
                }
            }

            return TranslateToArchitecture(_processHelper.GetCurrentProcessArchitecture());
        }

        static Architecture TranslateToArchitecture(PlatformArchitecture targetArchitecture)
        {
            switch (targetArchitecture)
            {
                case PlatformArchitecture.X86:
                    return Architecture.X86;
                case PlatformArchitecture.X64:
                    return Architecture.X64;
                case PlatformArchitecture.ARM:
                    return Architecture.ARM;
                case PlatformArchitecture.ARM64:
                    return Architecture.ARM64;
                case PlatformArchitecture.S390x:
                    return Architecture.S390x;
                default:
                    EqtTrace.Error($"TestRequestManager.TranslateToArchitecture: Unhandled architecture '{targetArchitecture}'.");
                    break;
            }

            // We prefer to not throw in case of unhandled architecture but return Default,
            // it should be handled in a correct way by the callers.
            return Architecture.Default;
        }
    }

    private bool AddOrUpdateConsoleLogger(
        XmlDocument document,
        RunConfiguration runConfiguration,
        LoggerRunSettings loggerRunSettings)
    {
        // Update console logger settings.
        bool consoleLoggerUpdated = UpdateConsoleLoggerIfExists(document, loggerRunSettings);

        // In case of CLI, add console logger if not already present.
        bool designMode = runConfiguration.DesignModeSet
            ? runConfiguration.DesignMode
            : _commandLineOptions.IsDesignMode;
        if (!designMode && !consoleLoggerUpdated)
        {
            AddConsoleLogger(document, loggerRunSettings);
        }

        // Update is required:
        //     1) in case of CLI;
        //     2) in case of design mode if console logger is present in runsettings.
        return !designMode || consoleLoggerUpdated;
    }

    private static bool UpdateTargetDevice(
        XPathNavigator navigator,
        XmlDocument document)
    {
        if (InferRunSettingsHelper.TryGetDeviceXml(navigator, out string? deviceXml))
        {
            InferRunSettingsHelper.UpdateTargetDevice(document, deviceXml);
            return true;
        }

        return false;
    }

    private bool UpdateCollectSourceInformation(
        XmlDocument document,
        RunConfiguration runConfiguration)
    {
        bool updateRequired = !runConfiguration.CollectSourceInformationSet;
        if (updateRequired)
        {
            InferRunSettingsHelper.UpdateCollectSourceInformation(
                document,
                _commandLineOptions.ShouldCollectSourceInformation);
        }
        return updateRequired;
    }

    private bool UpdateDesignMode(XmlDocument document, RunConfiguration runConfiguration)
    {
        // If user is already setting DesignMode via runsettings or CLI args; we skip.
        bool updateRequired = !runConfiguration.DesignModeSet;
        if (updateRequired)
        {
            InferRunSettingsHelper.UpdateDesignMode(
                document,
                _commandLineOptions.IsDesignMode);
        }
        return updateRequired;
    }

    private void CheckSourcesForCompatibility(
        Framework chosenFramework,
        Architecture chosenPlatform,
        Architecture defaultArchitecture,
        IDictionary<string, Architecture> sourcePlatforms,
        IDictionary<string, Framework> sourceFrameworks,
        IBaseTestEventsRegistrar? registrar)
    {
        // Find compatible sources.
        var compatibleSources = InferRunSettingsHelper.FilterCompatibleSources(
            chosenPlatform,
            defaultArchitecture,
            chosenFramework,
            sourcePlatforms,
            sourceFrameworks,
            out var incompatibleSettingWarning);

        // Raise warnings for incompatible sources
        if (!incompatibleSettingWarning.IsNullOrEmpty())
        {
            EqtTrace.Warning(incompatibleSettingWarning);
            registrar?.LogWarning(incompatibleSettingWarning);
        }

        // Log compatible sources
        EqtTrace.Info("Compatible sources list: ");
        EqtTrace.Info(string.Join("\n", compatibleSources.ToArray()));
    }

    private bool UpdatePlatform(
        XmlDocument document,
        XPathNavigator navigator,
        IList<string?>? sources,
        Architecture defaultArchitecture,
        out Architecture commonPlatform,
        out IDictionary<string, Architecture> sourceToPlatformMap)
    {
        // Get platform from runsettings. If runsettings specify a platform, we don't need to
        // auto detect it and update it, because it is forced by run settings to be a single given platform
        // for all the provided sources.
        bool platformSetByRunsettings = IsPlatformSetByRunSettings(navigator, out commonPlatform);

        if (platformSetByRunsettings)
        {
            EqtTrace.Info($"Platform is set by runsettings to be '{commonPlatform}' for all sources.");
            // Autodetect platforms from sources, so we can check that they are compatible with the settings, and report
            // incompatibilities as warnings.
            //
            // DO NOT overwrite the common platform, the one forced by runsettings should be used.
            var _ = _inferHelper.AutoDetectArchitecture(sources, defaultArchitecture, out sourceToPlatformMap);

            // If we would not want to report the incompatibilities later, we would simply return dictionary populated to the
            // platform that is set by the settings.
            //
            // sourceToPlatformMap = new Dictionary<string, Architecture>();
            // foreach (var source in sources)
            // {
            //     sourceToPlatformMap.Add(source, commonPlatform);
            // }

            // Return false, because we did not update runsettings.
            return false;
        }

        // Autodetect platform from sources, and return a single common platform.
        commonPlatform = _inferHelper.AutoDetectArchitecture(sources, defaultArchitecture, out sourceToPlatformMap);
        InferRunSettingsHelper.UpdateTargetPlatform(document, commonPlatform.ToString(), overwrite: true);

        EqtTrace.Info($"Platform was updated to '{commonPlatform}'.");
        // Return true because we updated runsettings.
        return true;
    }

    private bool UpdateFrameworkInRunSettingsIfRequired(
        XmlDocument document,
        XPathNavigator navigator,
        IList<string?>? sources,
        IBaseTestEventsRegistrar? registrar,
        out Framework commonFramework,
        out IDictionary<string, Framework> sourceToFrameworkMap)
    {
        bool frameworkSetByRunsettings = IsFrameworkSetByRunSettings(navigator, out commonFramework);

        if (frameworkSetByRunsettings)
        {
            // Autodetect frameworks from sources, so we can check that they are compatible with the settings, and report
            // incompatibilities as warnings.
            //
            // DO NOT overwrite the common framework, the one forced by runsettings should be used.
            var _ = _inferHelper.AutoDetectFramework(sources, out sourceToFrameworkMap);

            // If we would not want to report the incompatibilities later, we would simply return dictionary populated to the
            // framework that is set by the settings.
            //
            // sourceToFrameworkMap = new Dictionary<string, Framework>();
            // foreach (var source in sources)
            // {
            //     sourceToFrameworkMap.Add(source, commonFramework);
            // }

            WriteWarningForNetFramework35IsUnsupported(registrar, commonFramework);
            // Return false because we did not update runsettings.
            return false;
        }

        // Autodetect framework from sources, and return a single common platform.
        commonFramework = _inferHelper.AutoDetectFramework(sources, out sourceToFrameworkMap);
        InferRunSettingsHelper.UpdateTargetFramework(document, commonFramework.ToString(), overwrite: true);

        WriteWarningForNetFramework35IsUnsupported(registrar, commonFramework);

        // Return true because we updated runsettings.
        return true;
    }

    private static void WriteWarningForNetFramework35IsUnsupported(IBaseTestEventsRegistrar? registrar, Framework commonFramework)
    {
        // Raise warnings for unsupported frameworks.
        // TODO: Look at the sourceToFrameworkMap, and report paths to the sources that use that framework, rather than the chosen framework
        if (ObjectModel.Constants.DotNetFramework35.Equals(commonFramework.Name))
        {
            EqtTrace.Warning("TestRequestManager.UpdateRunSettingsIfRequired: throw warning on /Framework:Framework35 option.");
            registrar?.LogWarning(Resources.Resources.Framework35NotSupported);
        }
    }

    /// <summary>
    /// Add console logger in runsettings.
    /// </summary>
    /// <param name="document">Runsettings document.</param>
    /// <param name="loggerRunSettings">Logger run settings.</param>
    private void AddConsoleLogger(XmlDocument document, LoggerRunSettings loggerRunSettings)
    {
        var consoleLogger = new LoggerSettings
        {
            FriendlyName = ConsoleLogger.FriendlyName,
            Uri = new Uri(ConsoleLogger.ExtensionUri),
            AssemblyQualifiedName = typeof(ConsoleLogger).AssemblyQualifiedName,
            CodeBase = typeof(ConsoleLogger).GetTypeInfo().Assembly.Location,
            IsEnabled = true
        };

        loggerRunSettings.LoggerSettingsList.Add(consoleLogger);
        RunSettingsProviderExtensions.UpdateRunSettingsXmlDocumentInnerXml(
            document,
            ObjectModel.Constants.LoggerRunSettingsName,
            loggerRunSettings.ToXml().InnerXml);
    }

    /// <summary>
    /// Add console logger in runsettings if exists.
    /// </summary>
    /// <param name="document">Runsettings document.</param>
    /// <param name="loggerRunSettings">Logger run settings.</param>
    /// <returns>True if updated console logger in runsettings successfully.</returns>
    private bool UpdateConsoleLoggerIfExists(
        XmlDocument document,
        LoggerRunSettings loggerRunSettings)
    {
        var defaultConsoleLogger = new LoggerSettings
        {
            FriendlyName = ConsoleLogger.FriendlyName,
            Uri = new Uri(ConsoleLogger.ExtensionUri)
        };

        var existingLoggerIndex = loggerRunSettings.GetExistingLoggerIndex(defaultConsoleLogger);

        // Update assemblyQualifiedName and codeBase of existing logger.
        if (existingLoggerIndex >= 0)
        {
            var consoleLogger = loggerRunSettings.LoggerSettingsList[existingLoggerIndex];
            consoleLogger.AssemblyQualifiedName = typeof(ConsoleLogger).AssemblyQualifiedName;
            consoleLogger.CodeBase = typeof(ConsoleLogger).GetTypeInfo().Assembly.Location;
            RunSettingsProviderExtensions.UpdateRunSettingsXmlDocumentInnerXml(
                document,
                ObjectModel.Constants.LoggerRunSettingsName,
                loggerRunSettings.ToXml().InnerXml);

            return true;
        }

        return false;
    }

    private void RunTests(
        IRequestData requestData,
        TestRunCriteria testRunCriteria,
        ITestRunEventsRegistrar testRunEventsRegistrar,
        TestPlatformOptions? options,
        Dictionary<string, SourceDetail> sourceToSourceDetailMap)
    {
        // Make sure to run the run request inside a lock as the below section is not thread-safe.
        // TranslationLayer can process faster as it directly gets the raw un-serialized messages
        // whereas below logic needs to deserialize and do some cleanup.
        // While this section is cleaning up, TranslationLayer can trigger run causing multiple
        // threads to run the below section at the same time.
        lock (_syncObject)
        {
            try
            {
                _currentTestRunRequest = _testPlatform.CreateTestRunRequest(
                    requestData,
                    testRunCriteria,
                    options,
                    sourceToSourceDetailMap);

                _testRunResultAggregator.RegisterTestRunEvents(_currentTestRunRequest);
                testRunEventsRegistrar?.RegisterTestRunEvents(_currentTestRunRequest);

                _testPlatformEventSource.ExecutionRequestStart();

                _currentTestRunRequest.ExecuteAsync();

                // Wait for the run completion event
                _currentTestRunRequest.WaitForCompletion();
            }
            catch (Exception ex)
            {
                EqtTrace.Error("TestRequestManager.RunTests: failed to run tests: {0}", ex);
                _testRunResultAggregator.MarkTestRunFailed();
                throw;
            }
            finally
            {
                if (_currentTestRunRequest != null)
                {
                    _testRunResultAggregator.UnregisterTestRunEvents(_currentTestRunRequest);
                    testRunEventsRegistrar?.UnregisterTestRunEvents(_currentTestRunRequest);

                    _currentTestRunRequest.Dispose();
                    _currentTestRunRequest = null;
                }
            }
        }
    }

    /// <summary>
    /// Check runsettings, to see if framework was specified by the user, if yes then use that for all sources.
    /// This method either looks at runsettings directly when running as a server (DesignMode / IDE / via VSTestConsoleWrapper, or how you wanna call it)
    /// or uses the pre-parsed runsettings when in console mode.
    /// </summary>
    /// <param name="navigator"></param>
    /// <returns></returns>
    private bool IsFrameworkSetByRunSettings(
        XPathNavigator navigator,
        out Framework chosenFramework)
    {

        if (_commandLineOptions.IsDesignMode)
        {
            bool isValidFrameworkXml = InferRunSettingsHelper.TryGetFrameworkXml(navigator, out var frameworkXml);
            var runSettingsHaveValidFramework = isValidFrameworkXml && !frameworkXml.IsNullOrWhiteSpace();
            if (runSettingsHaveValidFramework)
            {
                // TODO: this should just ask the runsettings to give that value so we always parse it the same way
                chosenFramework = Framework.FromString(frameworkXml);
                return true;
            }

            chosenFramework = Framework.DefaultFramework;
            return false;
        }

        if (_commandLineOptions.FrameworkVersionSpecified)
        {
            chosenFramework = _commandLineOptions.TargetFrameworkVersion;
            return true;
        }

        chosenFramework = Framework.DefaultFramework;
        return false;
    }

    /// <summary>
    /// Check runsettings, to see if platform was specified by the user, if yes then use that for all sources.
    /// This method either looks at runsettings directly when running as a server (DesignMode / IDE / via VSTestConsoleWrapper, or how you wanna call it)
    /// or uses the pre-parsed runsettings when in console mode.
    /// </summary>
    /// <param name="navigator"></param>
    /// <returns></returns>
    private bool IsPlatformSetByRunSettings(
        XPathNavigator navigator, out Architecture chosenPlatform)
    {
        if (_commandLineOptions.IsDesignMode)
        {
            bool isValidPlatformXml = InferRunSettingsHelper.TryGetPlatformXml(
                navigator,
                out var platformXml);

            bool runSettingsHaveValidPlatform = isValidPlatformXml && !platformXml.IsNullOrWhiteSpace();
            if (runSettingsHaveValidPlatform)
            {
                // TODO: this should be checking if the enum has the value specified, or ideally just ask the runsettings to give that value
                // so we parse the same way always
                chosenPlatform = (Architecture)Enum.Parse(typeof(Architecture), platformXml, ignoreCase: true);
                return true;
            }

            chosenPlatform = Architecture.Default;
            return false;
        }

        if (_commandLineOptions.ArchitectureSpecified)
        {
            chosenPlatform = _commandLineOptions.TargetArchitecture;
            return true;
        }

        chosenPlatform = Architecture.Default;
        return false;
    }

    /// <summary>
    /// Collect metrics.
    /// </summary>
    /// <param name="requestData">Request data for common Discovery/Execution services.</param>
    /// <param name="runConfiguration">Run configuration.</param>
    private void CollectMetrics(IRequestData requestData, RunConfiguration runConfiguration)
    {
        // Collecting Target Framework.
        requestData.MetricsCollection.Add(
            TelemetryDataConstants.TargetFramework,
            runConfiguration.TargetFramework.Name);

        // Collecting Target Platform.
        requestData.MetricsCollection.Add(
            TelemetryDataConstants.TargetPlatform,
            runConfiguration.TargetPlatform.ToString());

        // Collecting Max CPU count.
        requestData.MetricsCollection.Add(
            TelemetryDataConstants.MaxCPUcount,
            runConfiguration.MaxCpuCount);

        // Collecting Target Device. Here, it will be updated run settings so, target device
        // will be under run configuration only.
        var targetDevice = runConfiguration.TargetDevice;
        if (targetDevice.IsNullOrEmpty())
        {
            requestData.MetricsCollection.Add(
                TelemetryDataConstants.TargetDevice,
                "Local Machine");
        }
        else if (targetDevice.Equals("Device", StringComparison.Ordinal)
                 || targetDevice.Contains("Emulator"))
        {
            requestData.MetricsCollection.Add(
                TelemetryDataConstants.TargetDevice,
                targetDevice);
        }
        else
        {
            // For IOT scenarios.
            requestData.MetricsCollection.Add(
                TelemetryDataConstants.TargetDevice,
                "Other");
        }

        // Collecting TestPlatform version.
        requestData.MetricsCollection.Add(
            TelemetryDataConstants.TestPlatformVersion,
            Product.Version);

        // Collecting TargetOS.
        requestData.MetricsCollection.Add(
            TelemetryDataConstants.TargetOS,
            new PlatformEnvironment().OperatingSystemVersion);

        //Collecting DisableAppDomain.
        requestData.MetricsCollection.Add(
            TelemetryDataConstants.DisableAppDomain,
            runConfiguration.DisableAppDomain);

    }

    /// <summary>
    /// Checks whether Telemetry opted in or not.
    /// By Default opting out
    /// </summary>
    /// <returns>Returns Telemetry Opted out or not</returns>
    private static bool IsTelemetryOptedIn()
        => Environment.GetEnvironmentVariable("VSTEST_TELEMETRY_OPTEDIN")?.Equals("1", StringComparison.Ordinal) == true;

    /// <summary>
    /// Log Command Line switches for Telemetry purposes
    /// </summary>
    /// <param name="requestData">Request Data providing common discovery/execution services.</param>
    private void LogCommandsTelemetryPoints(IRequestData requestData)
    {
        var commandsUsed = new List<string>();

        var parallel = _commandLineOptions.Parallel;
        if (parallel)
        {
            commandsUsed.Add("/Parallel");
        }

        var platform = _commandLineOptions.ArchitectureSpecified;
        if (platform)
        {
            commandsUsed.Add("/Platform");
        }

        var enableCodeCoverage = _commandLineOptions.EnableCodeCoverage;
        if (enableCodeCoverage)
        {
            commandsUsed.Add("/EnableCodeCoverage");
        }

        var inIsolation = _commandLineOptions.InIsolation;
        if (inIsolation)
        {
            commandsUsed.Add("/InIsolation");
        }

        var useVsixExtensions = _commandLineOptions.UseVsixExtensions;
        if (useVsixExtensions)
        {
            commandsUsed.Add("/UseVsixExtensions");
        }

        var frameworkVersionSpecified = _commandLineOptions.FrameworkVersionSpecified;
        if (frameworkVersionSpecified)
        {
            commandsUsed.Add("/Framework");
        }

        var settings = _commandLineOptions.SettingsFile;
        if (!settings.IsNullOrEmpty())
        {
            var extension = Path.GetExtension(settings);
            if (string.Equals(extension, ".runsettings", StringComparison.OrdinalIgnoreCase))
            {
                commandsUsed.Add("/settings//.RunSettings");
            }
            else if (string.Equals(extension, ".testsettings", StringComparison.OrdinalIgnoreCase))
            {
                commandsUsed.Add("/settings//.TestSettings");
            }
            else if (string.Equals(extension, ".vsmdi", StringComparison.OrdinalIgnoreCase))
            {
                commandsUsed.Add("/settings//.vsmdi");
            }
            else if (string.Equals(extension, ".testrunConfig", StringComparison.OrdinalIgnoreCase))
            {
                commandsUsed.Add("/settings//.testrunConfig");
            }
        }

        requestData.MetricsCollection.Add(
            TelemetryDataConstants.CommandLineSwitches,
            string.Join(",", commandsUsed.ToArray()));
    }

    /// <summary>
    /// Gets request data.
    /// </summary>
    /// <param name="protocolConfig">Protocol config.</param>
    /// <returns>Request data.</returns>
    private IRequestData GetRequestData(ProtocolConfig protocolConfig)
    {
        return new RequestData
        {
            ProtocolConfig = protocolConfig,
            MetricsCollection =
                _telemetryOptedIn || IsTelemetryOptedIn()
                    ? new MetricsCollection()
                    : new NoOpMetricsCollection(),
            IsTelemetryOptedIn = _telemetryOptedIn || IsTelemetryOptedIn()
        };
    }

    private static List<string?> GetSources(TestRunRequestPayload testRunRequestPayload)
    {
        // TODO: This should also use hashset to only return distinct sources.
        List<string?> sources = new();
        if (testRunRequestPayload.Sources != null
            && testRunRequestPayload.Sources.Count > 0)
        {
            sources = testRunRequestPayload.Sources;
        }
        else if (testRunRequestPayload.TestCases != null
                 && testRunRequestPayload.TestCases.Count > 0)
        {
            ISet<string?> sourcesSet = new HashSet<string?>();
            foreach (var testCase in testRunRequestPayload.TestCases)
            {
                sourcesSet.Add(testCase.Source);
            }
            sources = sourcesSet.ToList();
        }
        return sources;
    }
}
