package com.example.ridefixbro.model

data class CatalogBike(val id: Int, val make: String, val model: String, val year: Int)

data class GarageBike(
    val id: Int,
    val bikeId: Int,
    val make: String,
    val model: String,
    val year: Int,
    val createdAt: String
) {
    val label: String get() = "$make $model ($year)"
}

data class AddGarageBikeRequest(val bikeId: Int)

data class ManualPublicationJob(
    val id: String, val status: String, val make: String, val model: String, val year: Int,
    val manualKey: String, val collectionName: String, val skipPages: Int,
    val totalChunks: Int, val completedChunks: Int, val bikeId: Int?,
    val error: String?, val createdAt: String, val updatedAt: String, val completedAt: String?
) {
    val finished: Boolean get() = status == "Succeeded" || status == "Failed"
    val succeeded: Boolean get() = status == "Succeeded"
}
