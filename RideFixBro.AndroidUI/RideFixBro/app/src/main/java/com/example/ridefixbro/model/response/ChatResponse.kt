package com.example.ridefixbro.model.response

// sessionId ko next request mein use karo; 0 sirf new chat ke liye hai.
data class ChatResponse(
    val reply: String,
    val sessionId: Int
)

data class ChatErrorResponse(val sessionId: Int?)
