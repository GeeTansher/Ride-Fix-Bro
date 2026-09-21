package com.example.ridefixbro.network

import com.example.ridefixbro.model.request.ChatRequest
import com.example.ridefixbro.model.response.ChatResponse
import retrofit2.http.Body
import retrofit2.http.POST

interface RideFixApiInterface {
    // Ye apne .NET backend ka endpoint hai
    @POST("api/Chat/ask")
    suspend fun askMechanicBro(@Body request: ChatRequest): ChatResponse
}