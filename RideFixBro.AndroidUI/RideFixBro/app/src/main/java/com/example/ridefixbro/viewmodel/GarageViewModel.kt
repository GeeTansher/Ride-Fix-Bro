package com.example.ridefixbro.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.example.ridefixbro.auth.AuthRepository
import com.example.ridefixbro.model.AddGarageBikeRequest
import com.example.ridefixbro.model.CatalogBike
import com.example.ridefixbro.model.GarageBike
import com.example.ridefixbro.network.RideFixApiInterface
import com.example.ridefixbro.network.RideFixBroClient
import io.github.jan.supabase.auth.status.SessionStatus
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import retrofit2.HttpException

data class GarageUiState(
    val catalog: List<CatalogBike> = emptyList(),
    val bikes: List<GarageBike> = emptyList(),
    val loading: Boolean = false,
    val error: String? = null,
    val message: String? = null
)

class GarageViewModel(
    private val auth: AuthRepository,
    private val api: RideFixApiInterface = RideFixBroClient.api
) : ViewModel() {
    private val _state = MutableStateFlow(GarageUiState())
    val state = _state.asStateFlow()
    private var userId: String? = null
    private var job: Job? = null
    private var generation = 0
    private var ready = false

    init {
        viewModelScope.launch {
            try {
                auth.sessionStatus.collect { status ->
                    ready = status is SessionStatus.Authenticated
                    if (status is SessionStatus.Authenticated) {
                        val nextUser = status.session.user?.id
                        if (nextUser != userId) {
                            reset(nextUser)
                            refresh()
                        }
                    } else if (status is SessionStatus.NotAuthenticated) {
                        reset(null)
                    }
                }
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                reset(null)
                _state.value = GarageUiState(error = "Garage load nahi hui. Sign in aur connection check kar.")
            }
        }
    }

    private fun reset(nextUser: String?) {
        job?.cancel()
        generation++
        userId = nextUser
        _state.value = GarageUiState()
    }

    fun refresh() = request { token ->
        val catalog = api.bikes("Bearer $token")
        val bikes = api.garage("Bearer $token")
        currentCoroutineContext().ensureActive()
        _state.value = GarageUiState(catalog = catalog, bikes = bikes)
    }

    fun addBike(bikeId: Int) {
        if (_state.value.catalog.none { it.id == bikeId }) {
            _state.value = _state.value.copy(error = "Make, model aur year catalog se select kar.")
            return
        }
        request { token ->
            val added = api.addBike(AddGarageBikeRequest(bikeId), "Bearer $token")
            currentCoroutineContext().ensureActive()
            val bikes = (_state.value.bikes.filterNot { it.id == added.id } + added).sortedBy { it.id }
            _state.value = _state.value.copy(bikes = bikes, message = "Bike garage mein add ho gayi.")
        }
    }

    fun deleteBike(id: Int) = request { token ->
        api.deleteBike(id, "Bearer $token")
        currentCoroutineContext().ensureActive()
        _state.value = _state.value.copy(
            bikes = _state.value.bikes.filterNot { it.id == id },
            message = "Bike garage se remove ho gayi. Purani chats preserved hain."
        )
    }

    private fun request(action: suspend (String) -> Unit) {
        val currentUser = userId
        if (!ready || currentUser == null) {
            _state.value = _state.value.copy(error = "Session reconnect ho raha hai. Thodi der baad retry kar.")
            return
        }
        if (job?.isActive == true) return
        val requestGeneration = generation
        _state.value = _state.value.copy(loading = true, error = null, message = null)
        job = viewModelScope.launch {
            var token: String? = null
            try {
                token = auth.accessToken(currentUser)
                action(token)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                if (error is HttpException && error.code() == 401 && token != null) {
                    auth.invalidateSession(currentUser, token)
                }
                if (generation == requestGeneration) {
                    val text = when ((error as? HttpException)?.code()) {
                        400 -> "Bike selection valid nahi hai."
                        404 -> "Ye bike nahi mili. Garage refresh kar."
                        503 -> "Database abhi available nahi hai. Baad mein retry kar."
                        else -> "Garage request complete nahi hui. Connection check karke retry kar."
                    }
                    _state.value = _state.value.copy(error = text)
                }
            } finally {
                if (generation == requestGeneration) _state.value = _state.value.copy(loading = false)
            }
        }
    }
}
