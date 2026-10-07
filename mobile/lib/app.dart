import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'core/di/providers.dart';
import 'core/storage/secure_store.dart';
import 'features/app_initialization/application/startup_controller.dart';
import 'features/app_initialization/presentation/startup_screen.dart';
import 'features/diagnostics/api_round_trip.dart';
import 'features/diagnostics/device_key_diagnostics.dart';

/// The attendance application.
class ClockInXtraApp extends ConsumerStatefulWidget {
  /// Creates the application.
  const ClockInXtraApp({super.key});

  @override
  ConsumerState<ClockInXtraApp> createState() => _ClockInXtraAppState();
}

class _ClockInXtraAppState extends ConsumerState<ClockInXtraApp> {
  late final AppLifecycleListener _lifecycle;

  @override
  void initState() {
    super.initState();

    // Back in the foreground: the whole startup sequence runs again. The
    // employee may have walked away from the office, a device may have been
    // approved or revoked, or the day may have ticked over while the app sat in
    // the background. What was on screen before is not evidence of any of it
    // (§11, §65). A run already in progress — the location permission dialog
    // itself sends the app to the background briefly — is joined, not repeated.
    _lifecycle = AppLifecycleListener(
      onResume: () => ref.read(startupControllerProvider.notifier).restart(),
    );
  }

  @override
  void dispose() {
    _lifecycle.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'ClockInXtra',
      theme: ThemeData(colorScheme: ColorScheme.fromSeed(seedColor: Colors.indigo)),
      home: Builder(
        builder: (BuildContext context) => StartupScreen(
          actions: <Widget>[
            // Diagnostics exist to verify hardware and wire behaviour during
            // development. kDebugMode is a compile-time constant, so in a release
            // build this entry — and the screens behind it — are removed entirely.
            if (kDebugMode)
              IconButton(
                icon: const Icon(Icons.developer_mode),
                tooltip: 'Diagnostics',
                onPressed: () => Navigator.of(context).push(
                  MaterialPageRoute<void>(builder: (_) => const _Diagnostics()),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

/// Debug-only: the device-key and API round-trip checks.
class _Diagnostics extends ConsumerWidget {
  const _Diagnostics();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final SecureStore store = ref.read(secureStoreProvider);

    return DefaultTabController(
      length: 2,
      child: Scaffold(
        appBar: AppBar(
          title: const Text('Diagnostics'),
          bottom: const TabBar(tabs: <Widget>[Tab(text: 'Device key'), Tab(text: 'API round trip')]),
        ),
        body: TabBarView(
          children: <Widget>[
            DeviceKeyDiagnosticsPage(keyService: ref.read(deviceKeyServiceProvider)),
            ApiRoundTripPage(keyService: ref.read(deviceKeyServiceProvider), secureStore: store),
          ],
        ),
      ),
    );
  }
}
