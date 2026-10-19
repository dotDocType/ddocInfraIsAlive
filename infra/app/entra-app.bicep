extension microsoftGraphV1

param environmentName string

// Microsoft-wide well-known client ID of the Azure CLI. Pre-authorizing it lets an allowlisted user verify a
// deployment with `az account get-access-token` without a consent prompt (otherwise AADSTS65001).
var azureCliAppId = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'
var userImpersonationScopeId = guid('infraisalive', environmentName, 'user_impersonation')

resource apiApp 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: 'infraisalive-${environmentName}'
  displayName: 'InfraIsAlive API (${environmentName})'
  signInAudience: 'AzureADMyOrg'
  api: {
    requestedAccessTokenVersion: 2
    oauth2PermissionScopes: [
      {
        id: userImpersonationScopeId
        value: 'user_impersonation'
        type: 'User'
        isEnabled: true
        adminConsentDisplayName: 'Call InfraIsAlive'
        adminConsentDescription: 'Allows calling the InfraIsAlive ensure endpoint as the signed-in user.'
        userConsentDisplayName: 'Call InfraIsAlive'
        userConsentDescription: 'Allows calling the InfraIsAlive ensure endpoint as you.'
      }
    ]
    preAuthorizedApplications: [
      {
        appId: azureCliAppId
        delegatedPermissionIds: [userImpersonationScopeId]
      }
    ]
  }
}

resource apiServicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: apiApp.appId
}

output clientId string = apiApp.appId
