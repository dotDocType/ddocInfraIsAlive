param location string
@minLength(1)
param resourceToken string
param tags object

@description('Client ID of the Entra app registration that is the token audience.')
param apiClientId string

@description('Entra object IDs allowed to call the function (also enforced in code; an empty list denies everyone).')
param allowedCallerObjectIds array

@description('Application Insights connection string; empty disables telemetry export.')
param appInsightsConnectionString string

var appName = 'func-${resourceToken}'
var deploymentContainerName = 'app-package-${take(resourceToken, 7)}'
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${resourceToken}'
  location: location
  tags: tags
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${resourceToken}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
  }

  resource blobServices 'blobServices' = {
    name: 'default'

    resource deploymentContainer 'containers' = {
      name: deploymentContainerName
      properties: { publicAccess: 'None' }
    }
  }
}

resource storageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, storageBlobDataOwnerRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'plan-${resourceToken}'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: { tier: 'FlexConsumption', name: 'FC1' }
  properties: { reserved: true }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  tags: union(tags, { 'azd-service-name': 'api' })
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: { minTlsVersion: '1.2' }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identity.id
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40
        instanceMemoryMB: 512
      }
      runtime: { name: 'dotnet-isolated', version: '10.0' }
    }
  }

  resource appSettings 'config' = {
    name: 'appsettings'
    properties: union(
      {
        AzureWebJobsStorage__accountName: storage.name
        AzureWebJobsStorage__credential: 'managedidentity'
        AzureWebJobsStorage__clientId: identity.properties.clientId
        InfraIsAlive__ArmEndpoint: environment().resourceManager
        InfraIsAlive__ManagedIdentityClientId: identity.properties.clientId
      },
      empty(appInsightsConnectionString) ? {} : { APPLICATIONINSIGHTS_CONNECTION_STRING: appInsightsConnectionString }
    )
  }

  resource auth 'config' = {
    name: 'authsettingsV2'
    properties: {
      platform: { enabled: true }
      globalValidation: {
        requireAuthentication: true
        unauthenticatedClientAction: 'Return401'
      }
      login: { tokenStore: { enabled: false } }
      identityProviders: {
        azureActiveDirectory: {
          enabled: true
          registration: {
            clientId: apiClientId
            openIdIssuer: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
          }
          validation: {
            allowedAudiences: [apiClientId, 'api://${apiClientId}']
            defaultAuthorizationPolicy: {
              allowedPrincipals: { identities: allowedCallerObjectIds }
            }
          }
        }
      }
    }
  }
}

output principalId string = identity.properties.principalId
output url string = 'https://${functionApp.properties.defaultHostName}'
