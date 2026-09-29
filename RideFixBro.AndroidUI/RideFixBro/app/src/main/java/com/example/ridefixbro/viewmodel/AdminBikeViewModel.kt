package com.example.ridefixbro.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.example.ridefixbro.auth.AuthRepository
import com.example.ridefixbro.model.BikePublicationResponse
import com.example.ridefixbro.model.response.UserProfileResponse
import com.example.ridefixbro.network.RideFixApiInterface
import com.example.ridefixbro.network.RideFixBroClient
import com.google.gson.Gson
import com.google.gson.JsonParseException
import io.github.jan.supabase.auth.status.SessionStatus
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.RequestBody.Companion.toRequestBody
import retrofit2.HttpException
import java.io.IOException
import java.io.InterruptedIOException

data class AdminBikeForm(
    val make: String = "", val model: String = "", val year: String = "",
    val manualKey: String = "", val skipPages: String = "0"
) {
    val isValid: Boolean get() {
        val parsedYear = year.toIntOrNull() ?: return false
        val skipped = skipPages.toIntOrNull() ?: return false
        return make.trim().length in 1..100 && model.trim().length in 1..100 &&
            manualKey.trim().length in 1..128 && parsedYear in 1900..2100 && skipped in 0..999
    }
}

data class AdminBikeUiState(
    val form: AdminBikeForm = AdminBikeForm(),
    val pdfUri: String? = null,
    val pdfName: String? = null,
    val loading: Boolean = false,
    val error: String? = null,
    val published: BikePublicationResponse? = null
)

class AdminBikeViewModel(
    private val auth: AuthRepository,
    private val api: RideFixApiInterface = RideFixBroClient.uploadApi
) : ViewModel() {
    private val _state = MutableStateFlow(AdminBikeUiState())
    val state = _state.asStateFlow()
    private var userId: String? = null
    private var ready = false
    private var generation = 0
    private var upload: Job? = null

    init {
        viewModelScope.launch {
            try {
                auth.sessionStatus.collect { status ->
                    ready = status is SessionStatus.Authenticated
                    val nextUser = when (status) {
                        is SessionStatus.Authenticated -> status.session.user?.id
                        is SessionStatus.NotAuthenticated -> null
                        else -> userId
                    }
                    if (nextUser != userId) {
                        upload?.cancel()
                        generation++
                        userId = nextUser
                        _state.value = AdminBikeUiState()
                    }
                }
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                upload?.cancel()
                generation++
                userId = null
                ready = false
                _state.value = AdminBikeUiState(error = "Admin session unavailable. Sign in again.")
            }
        }
    }

    fun updateForm(form: AdminBikeForm) {
        if (!_state.value.loading) _state.update { it.copy(form = form, error = null) }
    }

    fun selectPdf(owner: String, uri: String, name: String) {
        if (owner == userId && !_state.value.loading)
            _state.update { it.copy(pdfUri = uri, pdfName = name, error = null) }
    }

    fun fileError(owner: String) {
        if (owner == userId) _state.update {
            it.copy(pdfUri = null, pdfName = null, error = "Selected PDF read nahi hui. Dobara select karo.")
        }
    }

    fun publish(profile: UserProfileResponse, file: MultipartBody.Part?) {
        if (_state.value.loading) return
        if (profile.role != "Admin" || profile.supabaseUserId != userId || !ready) {
            _state.update { it.copy(error = "Admin sign-in aur ready session required hai.") }
            return
        }
        val form = _state.value.form
        val year = form.year.toIntOrNull()
        val skip = form.skipPages.toIntOrNull()
        if (!form.isValid || year == null || skip == null || file == null || _state.value.pdfUri == null) {
            _state.update { it.copy(error = "Make, model, year (1900-2100), ManualKey, PDF aur valid skipPages (0-999) chahiye.") }
            return
        }
        val owner = profile.supabaseUserId
        val requestGeneration = ++generation
        _state.update { it.copy(loading = true, error = null, published = null) }
        upload = viewModelScope.launch {
            var token: String? = null
            try {
                token = auth.accessToken(owner)
                fun part(value: String) = value.toRequestBody("text/plain".toMediaType())
                val result = api.publishBike(part(form.make.trim()), part(form.model.trim()), part(year.toString()),
                    part(form.manualKey.trim()), part(skip.toString()), file, "Bearer $token")
                currentCoroutineContext().ensureActive()
                if (userId == owner && generation == requestGeneration) _state.update { it.copy(published = result) }
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                if (error is HttpException && error.code() == 401 && token != null) auth.invalidateSession(owner, token)
                if (userId == owner && generation == requestGeneration)
                    _state.update { it.copy(error = uploadError(error)) }
            } finally {
                if (userId == owner && generation == requestGeneration) _state.update { it.copy(loading = false) }
            }
        }
    }

    private fun uploadError(error: Exception): String {
        if (error is InterruptedIOException)
            return "Upload ka result confirm nahi hua. Processing stop hone ke baad same form se check/re-upload karo."
        if (error is HttpException) {
            val fallback = when (error.code()) {
                403 -> "Sirf SQL Admin role manual publish kar sakta hai."
                409 -> "Catalog/ManualKey conflict ya another upload running hai."
                413 -> "PDF 20 MiB se chhoti honi chahiye."
                400 -> "Form ya PDF invalid hai; scanned PDF ko pehle OCR chahiye."
                else -> "Publication complete confirm nahi hui. Same details se later retry karo."
            }
            try {
                val body = error.response()?.errorBody()?.use { Gson().fromJson(it.string(), UploadError::class.java) }
                return body?.error?.takeIf { it.isNotBlank() } ?: fallback
            } catch (parse: JsonParseException) {
                return "$fallback Server error response read nahi hui."
            } catch (read: IOException) {
                return "$fallback Server error response read nahi hui."
            }
        }
        return "PDF/upload read nahi hui. File access, 20 MiB limit aur connection check karo."
    }

    private data class UploadError(val error: String?)
}
