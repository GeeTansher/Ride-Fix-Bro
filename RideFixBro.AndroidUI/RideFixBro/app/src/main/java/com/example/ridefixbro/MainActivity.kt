package com.example.ridefixbro

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.ui.Modifier
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.example.ridefixbro.ui.theme.RideFixBroTheme
import com.example.ridefixbro.ui.pages.WorkspaceScreen
import com.example.ridefixbro.ui.pages.LoginScreen
import com.example.ridefixbro.viewmodel.AuthViewModel
import com.example.ridefixbro.viewmodel.ChatViewModel
import com.example.ridefixbro.viewmodel.GarageViewModel
import com.example.ridefixbro.viewmodel.AdminBikeViewModel

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val auth = (application as RideFixBroApplication).auth
        setContent {
            val authViewModel: AuthViewModel = viewModel(factory = viewModelFactory {
                initializer { AuthViewModel(auth) }
            })
            val chatViewModel: ChatViewModel = viewModel(factory = viewModelFactory {
                initializer { ChatViewModel(auth) }
            })
            val garageViewModel: GarageViewModel = viewModel(factory = viewModelFactory {
                initializer { GarageViewModel(auth) }
            })
            val adminBikeViewModel: AdminBikeViewModel = viewModel(factory = viewModelFactory {
                initializer { AdminBikeViewModel(auth) }
            })
            val authState by authViewModel.state.collectAsState()
            RideFixBroTheme {
                // A surface container using the 'background' color from the theme
                Surface(
                    modifier = Modifier.fillMaxSize(),
                    color = MaterialTheme.colorScheme.background
                ) {
                    if (authState.profile == null) {
                        LoginScreen(
                            authState,
                            onSignIn = { authViewModel.signIn(this@MainActivity) },
                            onRetry = { authViewModel.retry() },
                            onSignOut = { authViewModel.signOut() }
                        )
                    } else {
                        key(authState.profile!!.supabaseUserId) {
                            WorkspaceScreen(
                                authState.profile!!, authState, chatViewModel, garageViewModel, adminBikeViewModel,
                                onRetryAuth = authViewModel::retry, onSignOut = authViewModel::signOut
                            )
                        }
                    }
                }
            }
        }
    }
}