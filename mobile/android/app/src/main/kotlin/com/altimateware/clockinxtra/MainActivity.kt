package com.altimateware.clockinxtra

import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel

class MainActivity : FlutterActivity() {

    private companion object {
        const val DEVICE_KEY_CHANNEL = "com.altimateware.clockinxtra/device_key"
        const val INTEGRITY_CHANNEL = "com.altimateware.clockinxtra/integrity"
    }

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)

        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, DEVICE_KEY_CHANNEL)
            .setMethodCallHandler(DeviceKeyHandler())

        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, INTEGRITY_CHANNEL)
            .setMethodCallHandler(IntegrityHandler(applicationContext))
    }
}
