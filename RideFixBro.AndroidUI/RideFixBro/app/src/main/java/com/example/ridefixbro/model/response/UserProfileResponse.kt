package com.example.ridefixbro.model.response

data class UserProfileResponse(
    val id: Int,
    val supabaseUserId: String,
    val email: String,
    val role: String
)
