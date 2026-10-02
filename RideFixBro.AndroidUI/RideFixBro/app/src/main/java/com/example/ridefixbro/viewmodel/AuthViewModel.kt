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
import com.example.ridefixbro.auth.SessionNotReadyException
import com.example.ridefixbro.auth.SignInStage
import com.example.ridefixbro.auth.SignInStageException
import com.example.ridefixbro.model.response.UserProfileResponse
import com.example.ridefixbro.network.RideFixBroClient
import com.example.ridefixbro.network.RideFixApiInterface
import io.github.jan.supabase.auth.status.SessionStatus
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.launch
import retrofit2.HttpException
import java.io.InterruptedIOException

data class AuthUiState(
    val loading: Boolean = true,
    val signedIn: Boolean = false,
    val sessionReady: Boolean = false,
    val profile: UserProfileResponse? = null,
    val error: String? = null
)

class AuthViewModel(
    private val auth: AuthRepository,
    private val api: RideFixApiInterface = RideFixBroClient.api
) : ViewModel() {
    private val _state = MutableStateFlow(AuthUiState())
    val state = _state.asStateFlow()
    private var authJob: Job? = null
    private var sessionJob: Job? = null
    private var signingOut = false

    init {
        observeSession()
    }

    fun signIn(context: Context) = runAuth {
        auth.signIn(context)
    }

    fun retry() {
        if (authJob?.isActive == true) return
        // SDK restore/refresh khud karta hai. Retry sirf current status + API profile check karta hai.
        observeSession()
    }

    private fun observeSession() {
        sessionJob?.cancel()
        sessionJob = viewModelScope.launch {
            try {
                auth.sessionStatus
                    .distinctUntilChanged { previous, current ->
                        // Token refresh par same account ke liye /me baar-baar mat call karo.
                        previous is SessionStatus.Authenticated && current is SessionStatus.Authenticated &&
                            previous.session.user?.id == current.session.user?.id
                    }
                    .collectLatest { status ->
                        if (signingOut) return@collectLatest
                        when (status) {
                            SessionStatus.Initializing -> {
                                // App background mein gayi hai, logout nahi. Existing screen/profile rehne do.
                                val previous = _state.value
                                _state.value = previous.copy(
                                    loading = previous.signedIn || previous.error == null || authJob?.isActive == true,
                                    sessionReady = false
                                )
                            }
                            is SessionStatus.NotAuthenticated -> {
                                // Lifecycle/session invalidation must not erase the failure that caused this transition.
                                val previous = _state.value
                                val error = previous.error ?: if (previous.signedIn) {
                                    "Session verify nahi ho paayi. Dobara Google se sign in kar."
                                } else null
                                _state.value = AuthUiState(loading = error == null && authJob?.isActive == true, error = error)
                            }
                            is SessionStatus.RefreshFailure -> {
                                _state.value = _state.value.copy(
                                    loading = false, signedIn = true, sessionReady = false,
                                    error = "Session refresh nahi ho paayi. Internet check kar; SDK retry kar raha hai."
                                )
                            }
                            is SessionStatus.Authenticated -> {
                                val userId = status.session.user?.id
                                if (_state.value.profile?.supabaseUserId == userId && userId != null) {
                                    _state.value = _state.value.copy(loading = true, sessionReady = false, error = null)
                                } else {
                                    // Actual account change par old profile turant hatao.
                                    _state.value = AuthUiState(loading = true, signedIn = true)
                                }
                                try {
                                    if (userId == null) throw LoginRequiredException("Session has no user.")
                                    loadProfile(userId)
                                } catch (error: CancellationException) {
                                    throw error
                                } catch (error: Exception) {
                                    currentCoroutineContext().ensureActive()
                                    showError(error)
                                }
                            }
                        }
                    }
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                showError(error)
            }
        }
    }

    fun signOut() {
        if (authJob?.isActive == true) return
        signingOut = true
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
            } finally {
                signingOut = false
            }
        }
    }

    private suspend fun loadProfile(userId: String) {
        val token = auth.accessToken(userId)
        try {
            val profile = api.me("Bearer $token")
            currentCoroutineContext().ensureActive()
            check(profile.supabaseUserId == userId) { "The API returned a different account." }
            if (signingOut) return
            // collectLatest purane account ki pending profile request cancel kar deta hai.
            _state.value = AuthUiState(loading = false, signedIn = true, sessionReady = true, profile = profile)
        } catch (error: HttpException) {
            currentCoroutineContext().ensureActive()
            if (signingOut) return
            if (error.code() == 401) {
                // clearSession emits NotAuthenticated and can cancel this collectLatest block.
                showError(error)
                auth.invalidateSession(userId, token)
            }
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
                currentCoroutineContext().ensureActive()
                showError(error)
            }
        }
    }

    private fun showError(error: Exception) {
        val message = when (error) {
            is GetCredentialCancellationException ->
                "Google credential step complete nahi hua (GetCredentialCancellationException). Dobara account select kar."
            is SignInStageException -> {
                val type = error.cause?.javaClass?.simpleName ?: "UnknownError"
                when (error.stage) {
                    SignInStage.Google -> "Google credential step fail hua ($type). Account select karne ke baad bhi aaye toh Google sign-in configuration/Play Services check kar."
                    SignInStage.Supabase -> "Google credential mil gayi, lekin Supabase sign-in fail hua ($type). Supabase Auth logs aur network check kar."
                }
            }
            is AuthSetupException -> "Auth setup missing hai: local.properties mein SUPABASE_URL, SUPABASE_PUBLISHABLE_KEY aur GOOGLE_WEB_CLIENT_ID set kar."
            is NoCredentialException -> "Google account select nahi hua. Device ka Google account aur Play Services check kar."
            is LoginRequiredException -> "Session expire ho gaya. Google se dobara sign in kar."
            is SessionNotReadyException -> "Session restore/refresh chal raha hai. Internet check karke retry kar."
            is InterruptedIOException -> "Request 45 seconds mein complete nahi hui. Dobara try kar."
            is HttpException -> "Backend profile fetching api account verify nahi kar paayi (${error.code()}). Dobara sign in ya account connection retry kar."
            else -> "Sign-in complete nahi hua. Network/configuration check karke retry kar."
        }
        _state.value = _state.value.copy(loading = false, sessionReady = false, error = message)
    }
}
