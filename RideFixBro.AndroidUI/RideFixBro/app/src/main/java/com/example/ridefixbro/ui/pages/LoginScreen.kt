package com.example.ridefixbro.ui.pages

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.example.ridefixbro.viewmodel.AuthUiState

@Composable
fun LoginScreen(state: AuthUiState, onSignIn: () -> Unit, onRetry: () -> Unit, onSignOut: () -> Unit) {
    Column(
        modifier = Modifier.fillMaxSize().systemBarsPadding().padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Text("RideFix Bro", style = MaterialTheme.typography.headlineLarge)
        Text("Apni bike ki baat karne se pehle sign in kar, bro.")
        if (state.loading) {
            CircularProgressIndicator()
        } else if (state.signedIn) {
            Button(onClick = onRetry) { Text("Retry account connection") }
            TextButton(onClick = onSignOut) { Text("Sign out") }
        } else {
            Button(onClick = onSignIn) { Text("Sign in with Google") }
        }
        state.error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
    }
}
