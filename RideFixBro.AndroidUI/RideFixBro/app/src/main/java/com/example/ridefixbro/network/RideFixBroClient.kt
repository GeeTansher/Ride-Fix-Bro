package com.example.ridefixbro.network

import okhttp3.OkHttpClient
import retrofit2.Retrofit
import retrofit2.converter.gson.GsonConverterFactory
import java.util.concurrent.TimeUnit

object RideFixBroClient {
    // Agar Physical phone use kar raha hai wifi pe, toh apne PC ka local IP daalna (e.g., 192.168.1.5)
    // Agar Emulator hai toh 10.0.2.2 best hai. Port apna .NET wala daal diyo!
    private const val BASE_URL = "https://ridefixbroapi-cug3baevbedrfeh6.westus3-01.azurewebsites.net/"

    // Login/garage jaise normal requests ka wait 45 seconds.
    internal val apiClient = OkHttpClient.Builder()
        .connectTimeout(45, TimeUnit.SECONDS)
        .readTimeout(45, TimeUnit.SECONDS)
        .writeTimeout(45, TimeUnit.SECONDS)
        .callTimeout(45, TimeUnit.SECONDS)
        .retryOnConnectionFailure(false)
        .followRedirects(false)
        .followSslRedirects(false)
        .build()

    // Chat mein multiple tool calls ho sakti hain; total response ke liye 2 minutes do.
    internal val chatClient = apiClient.newBuilder()
        .readTimeout(120, TimeUnit.SECONDS)
        .callTimeout(120, TimeUnit.SECONDS)
        .build()

    val api: RideFixApiInterface by lazy { createApi(apiClient) }
    val chatApi: RideFixApiInterface by lazy { createApi(chatClient) }

    private fun createApi(client: OkHttpClient): RideFixApiInterface {
        return Retrofit.Builder()
            .baseUrl(BASE_URL)
            .client(client)
            .addConverterFactory(GsonConverterFactory.create())
            .build()
            .create(RideFixApiInterface::class.java)
    }
}