package com.example.ridefixbro.network

import okhttp3.OkHttpClient
import retrofit2.Retrofit
import retrofit2.converter.gson.GsonConverterFactory
import java.util.concurrent.TimeUnit

object RideFixBroClient {
    // Agar Physical phone use kar raha hai wifi pe, toh apne PC ka local IP daalna (e.g., 192.168.1.5)
    // Agar Emulator hai toh 10.0.2.2 best hai. Port apna .NET wala daal diyo!
    private const val BASE_URL = "https://ridefixbroapi-cug3baevbedrfeh6.westus3-01.azurewebsites.net/"

    // Retrofit ko thoda sabar sikhate hain as free tier used in azure (**GAREEB**) (60 seconds ka timeout)
    private val okHttpClient = OkHttpClient.Builder()
        .connectTimeout(60, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .writeTimeout(60, TimeUnit.SECONDS)
        .build()

    val api: RideFixApiInterface by lazy {
        Retrofit.Builder()
            .baseUrl(BASE_URL)
            .client(okHttpClient)
            .addConverterFactory(GsonConverterFactory.create())
            .build()
            .create(RideFixApiInterface::class.java)
    }
}