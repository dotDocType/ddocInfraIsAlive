targetScope = 'subscription'

@minLength(1)
@maxLength(40)
@description('azd environment name; used to name the resource group and resources.')
param environmentName string

@minLength(1)
@description('Region for all resources. Must support the Flex Consumption plan.')
param location string

@description('Client ID of a pre-created Entra app registration to use as the token audience. Leave empty to have the deployment create one (requires a directory role such as Application Developer).')
param existingApiClientId string = ''

@allowed(['true', 'false'])
@description('Whether to deploy Application Insights (string because azd substitutes parameters as text).')
param enableAppInsights string = 'true'

@description('Daily ingestion cap for the Log Analytics workspace, in GB (string because Bicep has no decimal type).')
param appInsightsDailyCapGb string = '0.1'

var adopterConfig = loadJsonContent('../config/infraisalive.json')
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var tags = { 'azd-env-name': environmentName }
var createApiApp = empty(existingApiClientId)

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module entraApp 'app/entra-app.bicep' = if (createApiApp) {
  name: 'entra-app'
  scope: rg
  params: {
    environmentName: environmentName
  }
}

module monitoring 'app/monitoring.bicep' = if (enableAppInsights == 'true') {
  name: 'monitoring'
  scope: rg
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
    dailyCapGb: appInsightsDailyCapGb
  }
}

var apiClientId = createApiApp ? entraApp!.outputs.clientId : existingApiClientId

module function 'app/function.bicep' = {
  name: 'function'
  scope: rg
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
    apiClientId: apiClientId
    allowedCallerObjectIds: adopterConfig.AllowedCallerObjectIds
    appInsightsConnectionString: enableAppInsights == 'true' ? monitoring!.outputs.connectionString : ''
  }
}

output AZURE_SUBSCRIPTION_ID string = subscription().subscriptionId
output AZURE_RESOURCE_GROUP string = rg.name
output FUNCTION_PRINCIPAL_ID string = function.outputs.principalId
output FUNCTION_URL string = function.outputs.url
output ARM_ENDPOINT string = environment().resourceManager
output API_CLIENT_ID string = apiClientId
output API_AUDIENCE string = apiClientId
