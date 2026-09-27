package com.example.ridefixbro.auth

import android.content.Context
import androidx.credentials.ClearCredentialStateRequest
import androidx.credentials.CredentialManager
import androidx.credentials.CustomCredential
import androidx.credentials.GetCredentialRequest
import com.example.ridefixbro.BuildConfig
import com.google.android.libraries.identity.googleid.GetSignInWithGoogleOption
import com.google.android.libraries.identity.googleid.GoogleIdTokenCredential
import io.github.jan.supabase.auth.Auth
import io.github.jan.supabase.auth.auth
import io.github.jan.supabase.auth.providers.Google
import io.github.jan.supabase.auth.providers.builtin.IDToken
import io.github.jan.supabase.auth.user.UserSession
import io.github.jan.supabase.createSupabaseClient
import io.github.jan.supabase.exceptions.RestException
import io.github.jan.supabase.logging.LogLevel
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.URI
import java.net.URISyntaxException
import java.security.MessageDigest
import java.util.UUID
import kotlin.time.Clock
import kotlin.time.Duration.Companion.seconds

@OptIn(kotlin.time.ExperimentalTime::class)
class AuthRepository(context: Context) {
    private val sessionManager = SecureSessionManager(context.applicationContext)
    private val credentialManager = CredentialManager.create(context.applicationContext)
    private val sessionLock = Mutex()
    private val _userId = MutableStateFlow<String?>(null)
    val userId = _userId.asStateFlow()

    private val supabase by lazy {
        validateAuthConfiguration(
            BuildConfig.SUPABASE_URL, BuildConfig.SUPABASE_PUBLISHABLE_KEY, BuildConfig.GOOGLE_WEB_CLIENT_ID
        )
        createSupabaseClient(BuildConfig.SUPABASE_URL, BuildConfig.SUPABASE_PUBLISHABLE_KEY) {
            // SDK debug logs session/token include kar sakte hain. App errors UI mein safely dikhayenge.
            defaultLogLevel = LogLevel.NONE
            install(Auth) {
                sessionManager = this@AuthRepository.sessionManager
                autoLoadFromStorage = false
                alwaysAutoRefresh = false
                enableLifecycleCallbacks = false
            }
        }
    }

    suspend fun restoreSession() = sessionLock.withLock {
        supabase.auth.awaitInitialization()
        supabase.auth.loadFromStorage(autoRefresh = false)
        val session = supabase.auth.currentSessionOrNull()
        _userId.value = session?.user?.id
        if (session != null) requireUsableSession()
    }

    suspend fun signIn(activityContext: Context) {
        // Config ko Google UI kholne se pehle validate karo.
        supabase.auth.awaitInitialization()
        val rawNonce = UUID.randomUUID().toString()
        val googleOption = GetSignInWithGoogleOption.Builder(BuildConfig.GOOGLE_WEB_CLIENT_ID)
            .setNonce(hashNonce(rawNonce))
            .build()
        val request = GetCredentialRequest.Builder().addCredentialOption(googleOption).build()
        val response = credentialManager.getCredential(activityContext, request)
        val credential = response.credential
        if (credential !is CustomCredential ||
            credential.type != GoogleIdTokenCredential.TYPE_GOOGLE_ID_TOKEN_CREDENTIAL) {
            throw LoginRequiredException("Google did not return an ID token.")
        }
        val googleToken = GoogleIdTokenCredential.createFrom(credential.data).idToken
        sessionLock.withLock {
            // Google ko hashed nonce diya; Supabase ko original nonce verification ke liye.
            supabase.auth.signInWith(IDToken) {
                provider = Google
                idToken = googleToken
                nonce = rawNonce
            }
            _userId.value = requireUsableSession().user?.id
                ?: throw LoginRequiredException("Login did not return a user.")
        }
    }

    suspend fun accessToken(expectedUserId: String): String = sessionLock.withLock {
        if (_userId.value != expectedUserId) throw LoginRequiredException("The signed-in account changed.")
        val session = requireUsableSession()
        if (session.user?.id != expectedUserId) {
            clearSession()
            throw LoginRequiredException("The signed-in account changed.")
        }
        session.accessToken
    }

    private suspend fun requireUsableSession(): UserSession {
        val current = supabase.auth.currentSessionOrNull()
            ?: throw LoginRequiredException("Sign in is required.")
        if (current.expiresAt <= Clock.System.now() + 60.seconds) {
            try {
                // Refresh once under the lock; concurrent requests must not reuse the refresh token.
                supabase.auth.refreshCurrentSession()
            } catch (error: RestException) {
                if (error.statusCode == 400 || error.statusCode == 401 || error.statusCode == 403) {
                    clearSession()
                    throw LoginRequiredException("The login session expired.")
                }
                throw error
            }
        }
        val session = supabase.auth.currentSessionOrNull()
            ?: throw LoginRequiredException("Sign in is required.")
        if (session.user?.id.isNullOrBlank() || session.accessToken.isBlank() ||
            session.expiresAt <= Clock.System.now() || session.user?.id != current.user?.id) {
            clearSession()
            throw LoginRequiredException("The login session is invalid.")
        }
        return session
    }

    suspend fun invalidateSession(expectedUserId: String) = sessionLock.withLock {
        if (_userId.value == expectedUserId) clearSession()
    }

    suspend fun signOut() {
        var remoteLogoutFailed = false
        sessionLock.withLock {
            _userId.value = null
            try {
                supabase.auth.signOut()
            } catch (error: Exception) {
                currentCoroutineContext().ensureActive()
                remoteLogoutFailed = true
            } finally {
                withContext(NonCancellable) { clearSession() }
            }
        }
        try {
            credentialManager.clearCredentialState(ClearCredentialStateRequest())
        } catch (error: Exception) {
            currentCoroutineContext().ensureActive()
            remoteLogoutFailed = true
        }
        if (remoteLogoutFailed) {
            // Local session clear hai. Already-issued access tokens expiry tak valid reh sakte hain.
            throw RemoteSignOutException()
        }
    }

    private suspend fun clearSession() {
        _userId.value = null
        supabase.auth.clearSession()
    }
}

class LoginRequiredException(message: String) : Exception(message)
class AuthSetupException : Exception("Set the three public auth values in local.properties.")
class RemoteSignOutException : IOException("Local logout completed, but remote cleanup could not be confirmed.")

internal fun validateAuthConfiguration(url: String, publishableKey: String, googleClientId: String) {
    val uri = try { URI(url) } catch (_: URISyntaxException) { throw AuthSetupException() }
    if (uri.scheme != "https" || uri.host.isNullOrBlank() || !uri.userInfo.isNullOrEmpty() ||
        !uri.query.isNullOrEmpty() || !uri.fragment.isNullOrEmpty() ||
        (uri.path != "" && uri.path != "/") || !publishableKey.startsWith("sb_publishable_") ||
        !googleClientId.endsWith(".apps.googleusercontent.com")) {
        throw AuthSetupException()
    }
}

internal fun hashNonce(rawNonce: String): String =
    MessageDigest.getInstance("SHA-256").digest(rawNonce.toByteArray(Charsets.UTF_8))
        .joinToString("") { "%02x".format(it) }
