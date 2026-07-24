package com.esousa.beam.security

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Vetor fixo compartilhado com o desktop (gerado por `Beam.Core.Security.ResumeHandshake`).
 *
 * As duas implementações precisam produzir exatamente os mesmos bytes: qualquer
 * divergência de HKDF, HMAC, ordem do salt ou rótulo faz a retomada falhar em
 * campo — e este teste é o que detecta isso antes do aparelho.
 */
class ResumeHandshakeTest {

    private val resumeKey = ByteArray(32) { it.toByte() }
    private val ecdhKey = ByteArray(32) { (0x40 + it).toByte() }
    private val clientNonce = ByteArray(32) { (0x80 + it).toByte() }
    private val serverNonce = ByteArray(32) { (0xC0 + it).toByte() }
    private val salt = ResumeHandshake.buildSalt(clientNonce, serverNonce)

    @Test
    fun deviceId_matchesDesktopVector() {
        assertEquals("867A05038787EB47", ResumeHandshake.computeDeviceId(resumeKey))
    }

    @Test
    fun sessionKey_matchesDesktopVector() {
        assertEquals(
            "CDF68AB5B02E187DC8E126CB9759256337FF6078E843E0B2EB0FB676D6A29C4A",
            ResumeHandshake.deriveSessionKey(ecdhKey, resumeKey, salt).toHex(),
        )
    }

    @Test
    fun proofs_matchDesktopVector() {
        assertEquals(
            "3C2711B05D37A97EF976D9E71860AC0F61ED7347B5D734192A3058D8A8759A57",
            ResumeHandshake.serverProof(resumeKey, salt).toHex(),
        )
        assertEquals(
            "2009323422FAC66623F715FEE31B0688FB7F6B4B867AB8FABE342A33FB73AD97",
            ResumeHandshake.clientProof(resumeKey, salt).toHex(),
        )
    }

    /** Trocar a ordem dos nonces mudaria a chave dos dois lados de forma incompatível. */
    @Test
    fun salt_isClientNonceThenServerNonce() {
        assertArrayEquals(clientNonce + serverNonce, salt)
    }

    @Test
    fun proofs_differBetweenSides() {
        assertFalse(
            ResumeHandshake.matches(
                ResumeHandshake.serverProof(resumeKey, salt),
                ResumeHandshake.clientProof(resumeKey, salt),
            ),
        )
    }

    @Test
    fun sessionKey_dependsOnResumeKey() {
        val other = ByteArray(32) { (it + 1).toByte() }
        assertFalse(
            ResumeHandshake.deriveSessionKey(ecdhKey, resumeKey, salt)
                .contentEquals(ResumeHandshake.deriveSessionKey(ecdhKey, other, salt)),
        )
    }

    @Test
    fun trustWindow_is72Hours() {
        assertEquals(72L * 60 * 60 * 1000, ResumeHandshake.TRUST_LIFETIME_MS)
        assertTrue(ResumeHandshake.NONCE_SIZE == 32)
    }

    private fun ByteArray.toHex(): String = joinToString("") { "%02X".format(it) }
}
