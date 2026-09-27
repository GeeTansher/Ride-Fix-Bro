package com.example.ridefixbro.viewmodel

import android.content.Context
import androidx.credentials.exceptions.GetCredentialCancellationException
import androidx.credentials.exceptions.NoCredentialException
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.example.ridefixbro.auth.AuthRepository
import com.example.ridefixbro.auth.AuthSetupException
import com.example.ridefixbro.auth.LoginRequiredException
import com.example.ridefixbro.auth.RemoteSignOutException
import com.example.ridefixbro.model.response.UserProfileResponse
import com.example.ridefixbro.network.RideFixBroClient
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.launch
import retrofit2.HttpException

data class AuthUiState(
    val loading: Boolean = true,
    val signedIn: Boolean = false,
    val profile: UserProfileResponse? = null,
    val error: String? = null
)

class AuthViewModel(private val auth: AuthRepository) : ViewModel() {
    private val _state = MutableStateFlow(AuthUiState())
    val state = _state.asStateFlow()
    private var authJob: Job? = null

    init {
        viewModelScope.launch {
            auth.userId.drop(1).collect { userId ->
                if (userId == null) {
                    _state.value = _state.value.copy(signedIn = false, profile = null)
                }
            }
        }
        runAuth {
            auth.restoreSession()
            loadProfile()
        }
    }

    fun signIn(context: Context) = runAuth {
        auth.signIn(context)
        loadProfile()
    }

    fun retry() = runAuth {
        auth.restoreSession()
        loadProfile()
    }

    fun signOut() {
        if (authJob?.isActive == true) return
        _state.value = AuthUiState(loading = true)
        authJob = viewModelScope.launch {
            try {
                auth.signOut()
                _state.value = AuthUiState(loading = false)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                _state.value = AuthUiState(
                    loading = false,
                    error = if (error is RemoteSignOutException) {
                        "Is device se logout ho gaya. Remote logout confirm nahi hua; internet check kar."
                    } else {
                        "Saved session cleanup complete nahi hui. App data clear karke dobara login kar."
                    }
                )
            }
        }
    }

    private suspend fun loadProfile() {
        val userId = auth.userId.value
        if (userId == null) {
            _state.value = AuthUiState(loading = false)
            return
        }
        val token = auth.accessToken(userId)
        try {
            val profile = RideFixBroClient.api.me("Bearer $token")
            check(profile.supabaseUserId == userId) { "The API returned a different account." }
            if (auth.userId.value == userId) {
                _state.value = AuthUiState(loading = false, signedIn = true, profile = profile)
            }
        } catch (error: HttpException) {
            if (error.code() == 401) auth.invalidateSession(userId)
            throw error
        }
    }

    private fun runAuth(action: suspend () -> Unit) {
        if (authJob?.isActive == true) return
        _state.value = AuthUiState(loading = true)
        authJob = viewModelScope.launch {
            try {
                action()
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                val message = when (error) {
                    is GetCredentialCancellationException -> null
                    is AuthSetupException -> "Auth setup missing hai: local.properties mein SUPABASE_URL, SUPABASE_PUBLISHABLE_KEY aur GOOGLE_WEB_CLIENT_ID set kar."
                    is NoCredentialException -> "Google account select nahi hua. Device ka Google account aur Play Services check kar."
                    is LoginRequiredException -> "Session expire ho gaya. Google se dobara sign in kar."
                    is HttpException -> "API account verify nahi kar paayi (${error.code()}). Retry kar."
                    else -> "Sign-in complete nahi hua. Network/configuration check karke retry kar."
                }
                _state.value = AuthUiState(loading = false, signedIn = auth.userId.value != null, error = message)
            }
        }
    }
}
