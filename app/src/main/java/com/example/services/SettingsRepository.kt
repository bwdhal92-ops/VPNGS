package com.example.services

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.example.models.AppSettings
import com.example.models.UserProfile
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

private val Context.dataStore: DataStore<Preferences> by preferencesDataStore(name = "vpngs_settings")

class SettingsRepository(private val context: Context) {

    private val json = Json { ignoreUnknownKeys = true }

    private val KEY_SETTINGS = stringPreferencesKey("app_settings_json")
    private val KEY_PROFILE = stringPreferencesKey("user_profile_json")
    private val KEY_TCP_MEASUREMENTS = stringPreferencesKey("tcp_measurements_json")

    val settingsFlow: Flow<AppSettings> = context.dataStore.data
        .map { prefs ->
            val jsonStr = prefs[KEY_SETTINGS]
            if (jsonStr != null) {
                try { json.decodeFromString<AppSettings>(jsonStr) } catch (e: Exception) { AppSettings() }
            } else AppSettings()
        }

    val profileFlow: Flow<UserProfile?> = context.dataStore.data
        .map { prefs ->
            val jsonStr = prefs[KEY_PROFILE]
            if (jsonStr != null) {
                try { json.decodeFromString<UserProfile>(jsonStr) } catch (e: Exception) { null }
            } else null
        }

    suspend fun saveSettings(settings: AppSettings) {
        context.dataStore.edit { prefs ->
            prefs[KEY_SETTINGS] = json.encodeToString(settings)
        }
    }

    suspend fun saveProfile(profile: UserProfile) {
        context.dataStore.edit { prefs ->
            prefs[KEY_PROFILE] = json.encodeToString(profile)
        }
    }

    suspend fun loadTcpMeasurements(): Map<String, Double> {
        val raw = context.dataStore.data.map { it[KEY_TCP_MEASUREMENTS] }.first() ?: return emptyMap()
        return try { json.decodeFromString<Map<String, Double>>(raw) } catch (_: Exception) { emptyMap() }
    }

    suspend fun saveTcpMeasurements(measurements: Map<String, Double>) {
        context.dataStore.edit { prefs ->
            prefs[KEY_TCP_MEASUREMENTS] = json.encodeToString(measurements)
        }
    }
}
