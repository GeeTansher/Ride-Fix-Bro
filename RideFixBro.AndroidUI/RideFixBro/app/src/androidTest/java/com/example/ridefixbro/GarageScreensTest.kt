package com.example.ridefixbro

import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import com.example.ridefixbro.model.CatalogBike
import com.example.ridefixbro.model.GarageBike
import com.example.ridefixbro.ui.pages.AddBikeScreen
import com.example.ridefixbro.ui.pages.GarageScreen
import com.example.ridefixbro.viewmodel.GarageUiState
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class GarageScreensTest {
    @get:Rule
    val compose = createComposeRule()

    @Test
    fun threeCatalogDropdownsSelectTheExactBikeId() {
        var selected: Int? = null
        val catalog = listOf(
            CatalogBike(101, "Harley-Davidson", "X440", 2024),
            CatalogBike(102, "Harley-Davidson", "X440", 2025)
        )
        compose.setContent {
            MaterialTheme {
                AddBikeScreen(GarageUiState(catalog = catalog), true, { selected = it }, {})
            }
        }
        compose.onNodeWithText("Add to garage").assertIsNotEnabled()
        compose.onNodeWithText("Make").performClick()
        compose.onNodeWithText("Harley-Davidson").performClick()
        compose.onNodeWithText("Model").performClick()
        compose.onNodeWithText("X440").performClick()
        compose.onNodeWithText("Year").performClick()
        compose.onNodeWithText("2025").performClick()
        compose.onNodeWithText("Add to garage").performClick()
        compose.runOnIdle { assertEquals(102, selected) }
    }

    @Test
    fun garageOffersConfirmedRemovalButNoAddOrEditControls() {
        var removed: Int? = null
        val bike = GarageBike(7, 101, "Harley-Davidson", "X440", 2024, "")
        compose.setContent {
            MaterialTheme {
                GarageScreen(GarageUiState(bikes = listOf(bike)), true, { removed = it }, {})
            }
        }
        compose.onNodeWithText("Add to garage").assertDoesNotExist()
        compose.onNodeWithText("Edit").assertDoesNotExist()
        compose.onNodeWithContentDescription("Remove ${bike.label}").performClick()
        compose.runOnIdle { assertEquals(null, removed) }
        compose.onNodeWithText("Remove", substring = false).performClick()
        compose.runOnIdle { assertEquals(7, removed) }
    }
}
