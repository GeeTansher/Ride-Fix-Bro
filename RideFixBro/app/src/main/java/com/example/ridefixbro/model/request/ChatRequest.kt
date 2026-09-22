package com.example.ridefixbro.model.request

// JSON request jo .NET ko jayegi
data class ChatRequest(
    val sessionId: String,
    val message: String,
    val imageData: String? = null // Optional hai, jab photo nahi hogi toh null jayega
)