#include <unity.h>
#include "provisioning_logic.h"

static void assertDecision(BootDecision decision, BootAction action, BootReason reason, bool clearRequest, bool resetCredentials) {
    TEST_ASSERT_EQUAL_INT((int)action, (int)decision.action);
    TEST_ASSERT_EQUAL_INT((int)reason, (int)decision.reason);
    TEST_ASSERT_EQUAL(clearRequest, decision.clearProvisioningRequest);
    TEST_ASSERT_EQUAL(resetCredentials, decision.resetStoredCredentials);
}

void test_provisioning_request_wins_over_everything() {
    const bool storedOptions[] = {false, true};
    const bool fallbackOptions[] = {false, true};
    for (bool stored : storedOptions) {
        for (bool fallback : fallbackOptions) {
            assertDecision(decideBootAction(true, stored, fallback),
                           BootAction::EnterProvisioning, BootReason::ProvisioningRequested, true, true);
        }
    }
}

void test_stored_credentials_win_over_dev_fallback() {
    assertDecision(decideBootAction(false, true, true),
                   BootAction::ConnectStoredCredentials, BootReason::StoredCredentials, false, false);
    assertDecision(decideBootAction(false, true, false),
                   BootAction::ConnectStoredCredentials, BootReason::StoredCredentials, false, false);
}

void test_dev_fallback_used_only_without_stored_credentials() {
    assertDecision(decideBootAction(false, false, true),
                   BootAction::ConnectDevFallback, BootReason::DevFallback, false, false);
}

void test_no_credentials_enters_provisioning() {
    assertDecision(decideBootAction(false, false, false),
                   BootAction::EnterProvisioning, BootReason::NoCredentials, false, false);
}

void test_pop_unavailable_state_names() {
    TEST_ASSERT_EQUAL_STRING("starting", popUnavailableStateName(NetworkState::Starting, CredentialSource::None));
    TEST_ASSERT_EQUAL_STRING("connecting", popUnavailableStateName(NetworkState::Connecting, CredentialSource::Stored));
    TEST_ASSERT_EQUAL_STRING("provisioned", popUnavailableStateName(NetworkState::Connected, CredentialSource::Stored));
    TEST_ASSERT_EQUAL_STRING("dev-fallback", popUnavailableStateName(NetworkState::Connected, CredentialSource::DevFallback));
    TEST_ASSERT_EQUAL_STRING("error", popUnavailableStateName(NetworkState::Error, CredentialSource::None));
}

void test_reconnect_backoff_doubles_and_clamps() {
    TEST_ASSERT_EQUAL_UINT32(5000, nextReconnectDelayMs(0, 5000, 60000));
    TEST_ASSERT_EQUAL_UINT32(10000, nextReconnectDelayMs(5000, 5000, 60000));
    TEST_ASSERT_EQUAL_UINT32(20000, nextReconnectDelayMs(10000, 5000, 60000));
    TEST_ASSERT_EQUAL_UINT32(60000, nextReconnectDelayMs(40000, 5000, 60000));
    TEST_ASSERT_EQUAL_UINT32(60000, nextReconnectDelayMs(60000, 5000, 60000));
    TEST_ASSERT_EQUAL_UINT32(60000, nextReconnectDelayMs(0xFFFFFFFFu, 5000, 60000));
}

void test_provisioning_start_failure_retries_then_stops() {
    TEST_ASSERT_EQUAL_INT((int)ProvisioningStartRecovery::RestartAndRetry,
                          (int)decideProvisioningStartRecovery(1, 3, false));
    TEST_ASSERT_EQUAL_INT((int)ProvisioningStartRecovery::RestartAndRetry,
                          (int)decideProvisioningStartRecovery(2, 3, true));
    TEST_ASSERT_EQUAL_INT((int)ProvisioningStartRecovery::GiveUp,
                          (int)decideProvisioningStartRecovery(3, 3, false));
    TEST_ASSERT_EQUAL_INT((int)ProvisioningStartRecovery::UseDevFallback,
                          (int)decideProvisioningStartRecovery(3, 3, true));
    TEST_ASSERT_EQUAL_INT((int)ProvisioningStartRecovery::GiveUp,
                          (int)decideProvisioningStartRecovery(0xFFFFFFFFu, 3, false));
}

void test_credential_failure_before_success_resets_for_retry() {
    TEST_ASSERT_EQUAL_INT((int)CredentialFailureAction::ResetForRetry, (int)decideCredentialFailureAction(false));
}

void test_credential_failure_after_success_keeps_credentials_and_stops() {
    TEST_ASSERT_EQUAL_INT((int)CredentialFailureAction::KeepCredentialsAndStopProvisioning,
                          (int)decideCredentialFailureAction(true));
}

void test_connected_while_provisioning_without_success_stays_provisioning() {
    TEST_ASSERT_EQUAL_INT((int)ConnectedTransition::StayProvisioning,
                          (int)decideConnectedTransition(NetworkState::Provisioning, false));
}

void test_connected_after_provisioning_success_becomes_connected_stored() {
    TEST_ASSERT_EQUAL_INT((int)ConnectedTransition::BecomeConnectedStored,
                          (int)decideConnectedTransition(NetworkState::Provisioning, true));
}

void test_connected_in_station_states_becomes_connected() {
    TEST_ASSERT_EQUAL_INT((int)ConnectedTransition::BecomeConnected,
                          (int)decideConnectedTransition(NetworkState::Connecting, false));
    TEST_ASSERT_EQUAL_INT((int)ConnectedTransition::BecomeConnected,
                          (int)decideConnectedTransition(NetworkState::Connecting, true));
    TEST_ASSERT_EQUAL_INT((int)ConnectedTransition::BecomeConnected,
                          (int)decideConnectedTransition(NetworkState::Error, false));
}

void runProvisioningLogicTests() {
    UNITY_BEGIN();
    RUN_TEST(test_connected_while_provisioning_without_success_stays_provisioning);
    RUN_TEST(test_connected_after_provisioning_success_becomes_connected_stored);
    RUN_TEST(test_connected_in_station_states_becomes_connected);
    RUN_TEST(test_credential_failure_before_success_resets_for_retry);
    RUN_TEST(test_credential_failure_after_success_keeps_credentials_and_stops);
    RUN_TEST(test_provisioning_start_failure_retries_then_stops);
    RUN_TEST(test_provisioning_request_wins_over_everything);
    RUN_TEST(test_stored_credentials_win_over_dev_fallback);
    RUN_TEST(test_dev_fallback_used_only_without_stored_credentials);
    RUN_TEST(test_no_credentials_enters_provisioning);
    RUN_TEST(test_pop_unavailable_state_names);
    RUN_TEST(test_reconnect_backoff_doubles_and_clamps);
    UNITY_END();
}

#ifdef ARDUINO
#include <Arduino.h>

void setup() {
    delay(2000);
    runProvisioningLogicTests();
}

void loop() {
}
#else
int main(int argc, char** argv) {
    (void)argc;
    (void)argv;
    runProvisioningLogicTests();
    return 0;
}
#endif
