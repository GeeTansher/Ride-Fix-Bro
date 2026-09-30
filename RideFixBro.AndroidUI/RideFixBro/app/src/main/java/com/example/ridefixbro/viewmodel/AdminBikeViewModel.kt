package com.example.ridefixbro.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.example.ridefixbro.auth.AuthRepository
import com.example.ridefixbro.model.ManualPublicationJob
import com.example.ridefixbro.model.response.UserProfileResponse
import com.example.ridefixbro.network.RideFixApiInterface
import com.example.ridefixbro.network.RideFixBroClient
import com.google.gson.Gson
import com.google.gson.JsonParseException
import io.github.jan.supabase.auth.status.SessionStatus
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
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
    val statusLoading: Boolean = false,
    val error: String? = null,
    val job: ManualPublicationJob? = null,
    val jobs: List<ManualPublicationJob> = emptyList()
) {
    val busy: Boolean get() = loading || job?.finished == false
}

class AdminBikeViewModel(
    private val auth: AuthRepository,
    private val api: RideFixApiInterface = RideFixBroClient.uploadApi,
    private val statusApi: RideFixApiInterface = RideFixBroClient.api
) : ViewModel() {
    private val _state = MutableStateFlow(AdminBikeUiState())
    val state = _state.asStateFlow()
    private var userId: String? = null
    private var ready = false
    private var generation = 0
    private var pollGeneration = 0
    private var upload: Job? = null
    private var polling: Job? = null
    private var visibleProfile: UserProfileResponse? = null

    init {
        viewModelScope.launch {
            try {
                auth.sessionStatus.collect { status ->
                    val wasReady = ready
                    ready = status is SessionStatus.Authenticated
                    val nextUser = when (status) {
                        is SessionStatus.Authenticated -> status.session.user?.id
                        is SessionStatus.NotAuthenticated -> null
                        else -> userId
                    }
                    if (nextUser != userId) {
                        upload?.cancel()
                        stopPolling()
                        generation++
                        userId = nextUser
                        if (visibleProfile?.supabaseUserId != nextUser) visibleProfile = null
                        _state.value = AdminBikeUiState()
                    } else if (!ready) stopPolling()
                    if (ready && !wasReady) refreshJobs()
                }
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                upload?.cancel()
                stopPolling()
                generation++
                userId = null
                ready = false
                _state.value = AdminBikeUiState(error = "Admin session unavailable. Sign in again.")
            }
        }
    }

    fun observeJobs(profile: UserProfileResponse) {
        visibleProfile = profile.takeIf { it.role == "Admin" }
        if (visibleProfile == null) stopPolling() else refreshJobs()
    }

    fun stopObserving() {
        visibleProfile = null
        stopPolling()
    }

    private fun stopPolling() {
        polling?.cancel()
        pollGeneration++
        _state.update { it.copy(statusLoading = false) }
    }

    fun updateForm(form: AdminBikeForm) {
        if (!_state.value.busy) _state.update { it.copy(form = form, error = null) }
    }

    fun selectPdf(owner: String, uri: String, name: String) {
        if (owner == userId && !_state.value.busy)
            _state.update { it.copy(pdfUri = uri, pdfName = name, error = null) }
    }

    fun fileError(owner: String) {
        if (owner == userId) _state.update {
            it.copy(pdfUri = null, pdfName = null, error = "Selected PDF read nahi hui. Dobara select karo.")
        }
    }

    fun refreshJobs() {
        val profile = visibleProfile ?: return
        if (!ready || profile.supabaseUserId != userId || _state.value.loading) return
        stopPolling()
        val requestGeneration = pollGeneration
        polling = viewModelScope.launch {
            var token: String? = null
            _state.update { it.copy(statusLoading = true, error = null) }
            try {
                token = auth.accessToken(profile.supabaseUserId)
                val jobs = statusApi.manualJobs("Bearer $token")
                currentCoroutineContext().ensureActive()
                if (!isPolling(profile.supabaseUserId, requestGeneration)) return@launch
                val selected = jobs.firstOrNull { it.id == _state.value.job?.id }
                    ?: jobs.firstOrNull { !it.finished } ?: jobs.firstOrNull()
                _state.update { it.copy(jobs = jobs, job = selected, statusLoading = false) }
                if (selected != null) pollUntilFinished(profile.supabaseUserId, requestGeneration, selected)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                handleStatusError(profile.supabaseUserId, requestGeneration, token, error)
            } finally {
                if (isPolling(profile.supabaseUserId, requestGeneration)) _state.update { it.copy(statusLoading = false) }
            }
        }
    }

    fun openJob(id: String) {
        val selected = _state.value.jobs.firstOrNull { it.id == id } ?: return
        _state.update { it.copy(job = selected, error = null) }
        refreshJobs()
    }

    private fun isPolling(owner: String, requestGeneration: Int) =
        userId == owner && visibleProfile?.supabaseUserId == owner && ready && pollGeneration == requestGeneration

    private suspend fun pollUntilFinished(owner: String, requestGeneration: Int, initial: ManualPublicationJob) {
        var job = initial
        while (!job.finished && isPolling(owner, requestGeneration)) {
            delay(3_000)
            val token = auth.accessToken(owner)
            try {
                val response = statusApi.manualJob(job.id, "Bearer $token")
                currentCoroutineContext().ensureActive()
                if (!isPolling(owner, requestGeneration)) return
                check(response.id == job.id) { "Different publication job returned." }
                job = response
                _state.update { current -> current.copy(job = response,
                    jobs = current.jobs.map { if (it.id == response.id) response else it }) }
            } catch (error: HttpException) {
                if (error.code() == 401) auth.invalidateSession(owner, token)
                throw error
            }
        }
    }

    private suspend fun handleStatusError(owner: String, requestGeneration: Int, token: String?, error: Exception) {
        if (error is HttpException && error.code() == 401 && token != null) auth.invalidateSession(owner, token)
        if (isPolling(owner, requestGeneration))
            _state.update { it.copy(error = "Job status refresh nahi hua. Processing may still continue; use Refresh jobs.") }
    }

    fun publish(profile: UserProfileResponse, file: MultipartBody.Part?) {
        if (_state.value.busy) return
        if (profile.role != "Admin" || profile.supabaseUserId != userId || !ready) {
            _state.update { it.copy(error = "Admin sign-in aur ready session required hai.") }
            return
        }
        val form = _state.value.form
        val year = form.year.toIntOrNull()
        val skip = form.skipPages.toIntOrNull()
        if (!form.isValid || year == null || skip == null || file == null || _state.value.pdfUri == null) {
            _state.update { it.copy(error = "Make, model, year, ManualKey, PDF aur valid skipPages chahiye.") }
            return
        }
        val owner = profile.supabaseUserId
        val requestGeneration = ++generation
        stopPolling()
        _state.update { it.copy(loading = true, error = null) }
        upload = viewModelScope.launch {
            var token: String? = null
            try {
                token = auth.accessToken(owner)
                fun part(value: String) = value.toRequestBody("text/plain".toMediaType())
                val job = api.publishBike(part(form.make.trim()), part(form.model.trim()), part(year.toString()),
                    part(form.manualKey.trim()), part(skip.toString()), file, "Bearer $token")
                currentCoroutineContext().ensureActive()
                if (userId == owner && generation == requestGeneration) _state.update {
                    it.copy(job = job, jobs = (listOf(job) + it.jobs).distinctBy { row -> row.id },
                        pdfUri = null, pdfName = null)
                }
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                if (error is HttpException && error.code() == 401 && token != null) auth.invalidateSession(owner, token)
                if (userId == owner && generation == requestGeneration)
                    _state.update { it.copy(error = uploadError(error)) }
            } finally {
                if (userId == owner && generation == requestGeneration) {
                    _state.update { it.copy(loading = false) }
                    // HTTP disconnect does not cancel a saved server job; recover it through the list if needed.
                    if (_state.value.error == null) refreshJobs()
                }
            }
        }
    }

    private fun uploadError(error: Exception): String {
        if (error is InterruptedIOException)
            return "Submission response nahi aaya. Refresh jobs before submitting again; server may have saved the job."
        if (error is HttpException) {
            val fallback = when (error.code()) {
                403 -> "Sirf SQL Admin role manual publish kar sakta hai."
                409 -> "An active job or catalog conflict exists. Refresh jobs before re-upload."
                413 -> "PDF/extracted text limit exceed hui."
                400 -> "Form ya PDF invalid hai; scanned PDF ko pehle OCR chahiye."
                else -> "Job submit confirm nahi hua. Refresh jobs before trying again."
            }
            try {
                val body = error.response()?.errorBody()?.use { Gson().fromJson(it.string(), UploadError::class.java) }
                return body?.error?.takeIf { it.isNotBlank() } ?: fallback
            } catch (parse: JsonParseException) { return "$fallback Server error response read nahi hui." }
            catch (read: IOException) { return "$fallback Server error response read nahi hui." }
        }
        return "PDF/job submission failed. File access and connection check karo; Refresh jobs before re-upload."
    }

    private data class UploadError(val error: String?)
}
