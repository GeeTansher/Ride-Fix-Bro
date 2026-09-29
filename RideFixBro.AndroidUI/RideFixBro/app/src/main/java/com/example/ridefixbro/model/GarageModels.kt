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
