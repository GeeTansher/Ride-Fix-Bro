package com.example.ridefixbro

import android.app.Application
import com.example.ridefixbro.auth.AuthRepository

class RideFixBroApplication : Application() {
    val auth by lazy { AuthRepository(this) }
}
