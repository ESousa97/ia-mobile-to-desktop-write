package com.esousa.beam

import android.app.Application
import com.esousa.beam.session.ClipBridgeSession

class ClipBridgeApplication : Application() {

    lateinit var session: ClipBridgeSession
        private set

    override fun onCreate() {
        super.onCreate()
        session = ClipBridgeSession(this)
    }
}
