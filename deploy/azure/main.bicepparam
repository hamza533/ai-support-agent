using './main.bicep'

// Example parameters. Secrets come from the pipeline (GitHub secrets → az deployment), never from this file.
param namePrefix = 'brightline'
param webImage = 'ghcr.io/your-org/brightline-web:0.1.0'
param legacyImage = 'ghcr.io/your-org/brightline-legacyorders:0.1.0'
param postgresAdminPassword = readEnvironmentVariable('POSTGRES_ADMIN_PASSWORD', '')
param legacyApiKey = readEnvironmentVariable('LEGACY_API_KEY', '')
param entraTenantId = ''
param entraClientId = ''
