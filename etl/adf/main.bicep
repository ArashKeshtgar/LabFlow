// LabFlow ETL, cloud leg: Azure Data Factory copies the de-identified
// warehouse from the on-prem LabFlow database (through a self-hosted
// integration runtime on the lab's server) into an Azure SQL database.
//
//   on-prem LabFlow.dw.*  --SHIR-->  ADF pipeline  -->  Azure SQL LabFlowDW (stg.* -> dw.*)
//
// Deploy with etl/adf/deploy.ps1, which also creates the database objects and
// the factory's database user (T-SQL that Bicep can't run) and registers the
// integration runtime node.

@description('Region for all resources.')
param location string = 'canadacentral'

@description('Globally unique Data Factory name.')
param factoryName string = 'labflow-adf-arash'

@description('Globally unique SQL server name; becomes <name>.database.windows.net.')
param sqlServerName string = 'labflow-arash-sql'

@description('Cloud warehouse database name.')
param databaseName string = 'LabFlowDW'

@description('Entra admin of the SQL server: user principal name.')
param sqlAdminLogin string

@description('Entra admin object id (az ad signed-in-user show --query id -o tsv).')
param sqlAdminObjectId string

@description('Public IP allowed through the SQL firewall so deploy.ps1 can run the setup script. Empty = none.')
param deployerIp string = ''

@description('On-prem SQL Server as the integration runtime host sees it.')
param onPremServer string = 'localhost'

@description('On-prem LabFlow database.')
param onPremDatabase string = 'LabFlow'

@description('Read-only on-prem login (dw schema and run logs only, never etl.Secret).')
param onPremUser string = 'adf_reader'

@secure()
@description('Password of the on-prem read-only login. Stored encrypted in the factory.')
param onPremPassword string

@description('Daily run time (Toronto), after the 02:00 SSIS job.')
param triggerHour int = 3

/* ------------------------------------------------------------------ SQL */

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: sqlServerName
  location: location
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true   // no SQL passwords in the cloud
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId
      principalType: 'User'
    }
  }
}

// "Allow Azure services": how the factory's Azure integration runtime reaches SQL.
resource allowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource allowDeployer 'Microsoft.Sql/servers/firewallRules@2023-08-01' = if (!empty(deployerIp)) {
  parent: sqlServer
  name: 'Deployer'
  properties: { startIpAddress: deployerIp, endIpAddress: deployerIp }
}

// Free offer (10 databases per subscription, 100,000 vCore-seconds and 32 GB a
// month); pauses instead of billing when the monthly allowance runs out.
resource db 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sqlServer
  name: databaseName
  location: location
  sku: { name: 'GP_S_Gen5_2', tier: 'GeneralPurpose', family: 'Gen5', capacity: 2 }
  properties: {
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368
    requestedBackupStorageRedundancy: 'Local'
  }
}

/* ------------------------------------------------------------------ factory */

resource adf 'Microsoft.DataFactory/factories@2018-06-01' = {
  name: factoryName
  location: location
  identity: { type: 'SystemAssigned' }   // its only way into Azure SQL
  properties: {}
}

resource shir 'Microsoft.DataFactory/factories/integrationRuntimes@2018-06-01' = {
  parent: adf
  name: 'ir-onprem-labflow'
  properties: {
    type: 'SelfHosted'
    description: 'Runs on the lab server next to SQL Server; reaches the on-prem LabFlow database.'
  }
}

resource lsOnPrem 'Microsoft.DataFactory/factories/linkedservices@2018-06-01' = {
  parent: adf
  name: 'LS_OnPrem_LabFlow'
  properties: {
    type: 'SqlServer'
    description: 'On-prem LabFlow, read-only login, through the self-hosted IR.'
    connectVia: { referenceName: shir.name, type: 'IntegrationRuntimeReference' }
    typeProperties: {
      server: onPremServer
      database: onPremDatabase
      encrypt: 'optional'
      trustServerCertificate: true
      authenticationType: 'SQL'
      userName: onPremUser
      password: { type: 'SecureString', value: onPremPassword }
    }
  }
}

resource lsAzure 'Microsoft.DataFactory/factories/linkedservices@2018-06-01' = {
  parent: adf
  name: 'LS_Azure_LabFlowDW'
  properties: {
    type: 'AzureSqlDatabase'
    description: 'Cloud warehouse, factory managed identity.'
    typeProperties: {
      server: '${sqlServer.name}${environment().suffixes.sqlServerHostname}'
      database: databaseName
      encrypt: 'mandatory'
      trustServerCertificate: false
      authenticationType: 'SystemAssignedManagedIdentity'
    }
  }
}

// Query-driven source: the table comes from each activity's query.
resource dsOnPrem 'Microsoft.DataFactory/factories/datasets@2018-06-01' = {
  parent: adf
  name: 'DS_OnPrem_Query'
  properties: {
    type: 'SqlServerTable'
    linkedServiceName: { referenceName: lsOnPrem.name, type: 'LinkedServiceReference' }
    typeProperties: {}
  }
}

resource dsAzureTable 'Microsoft.DataFactory/factories/datasets@2018-06-01' = {
  parent: adf
  name: 'DS_Azure_Table'
  properties: {
    type: 'AzureSqlTable'
    linkedServiceName: { referenceName: lsAzure.name, type: 'LinkedServiceReference' }
    parameters: { schema: { type: 'String' }, table: { type: 'String' } }
    typeProperties: {
      schema: { value: '@dataset().schema', type: 'Expression' }
      table: { value: '@dataset().table', type: 'Expression' }
    }
  }
}

/* ------------------------------------------------------------------ pipeline */

var onPremSource = { referenceName: dsOnPrem.name, type: 'DatasetReference' }
var wm = '@{activity(\'LKP_CopyWatermark\').output.firstRow.LastRunId}'
var maxRun = '@{activity(\'LKP_SourceMaxRun\').output.firstRow.MaxRunId}'

resource pipeline 'Microsoft.DataFactory/factories/pipelines@2018-06-01' = {
  parent: adf
  name: 'PL_LabFlow_DW_to_Azure'
  properties: {
    description: 'Dimensions and run logs in full, facts incrementally (runs above the cloud watermark), then one MERGE in Azure SQL.'
    parameters: {
      fullTables: {
        type: 'Array'
        defaultValue: [
          { src: 'dw.DimDate', sink: 'DimDate' }
          { src: 'dw.DimTest', sink: 'DimTest' }
          { src: 'dw.DimPayer', sink: 'DimPayer' }
          { src: 'dw.DimPatient', sink: 'DimPatient' }
          { src: 'etl.LoadRun', sink: 'LoadRun' }
          { src: 'etl.RejectRow', sink: 'RejectRow' }
        ]
      }
    }
    activities: [
      {
        name: 'LKP_SourceMaxRun'
        type: 'Lookup'
        description: 'Highest successful SSIS run on-prem: the upper bound of this copy.'
        policy: { timeout: '0.00:10:00', retry: 2, retryIntervalInSeconds: 60 }
        typeProperties: {
          source: {
            type: 'SqlServerSource'
            sqlReaderQuery: 'SELECT ISNULL(MAX(RunId), 0) AS MaxRunId FROM etl.LoadRun WHERE Status = \'Succeeded\''
          }
          dataset: onPremSource
          firstRowOnly: true
        }
      }
      {
        name: 'LKP_CopyWatermark'
        type: 'Lookup'
        description: 'Last on-prem run already copied (waits for the serverless database to resume).'
        policy: { timeout: '0.00:10:00', retry: 3, retryIntervalInSeconds: 60 }
        typeProperties: {
          source: {
            type: 'AzureSqlSource'
            sqlReaderQuery: 'SELECT Value AS LastRunId FROM etl.CopyWatermark WHERE Name = \'SourceLoadRunId\''
          }
          dataset: { referenceName: dsAzureTable.name, type: 'DatasetReference', parameters: { schema: 'etl', table: 'CopyWatermark' } }
          firstRowOnly: true
        }
      }
      {
        name: 'FE_CopyFullTables'
        type: 'ForEach'
        dependsOn: [ { activity: 'LKP_CopyWatermark', dependencyConditions: [ 'Succeeded' ] } ]
        typeProperties: {
          items: { value: '@pipeline().parameters.fullTables', type: 'Expression' }
          isSequential: false
          batchCount: 3
          activities: [
            {
              name: 'CP_FullTable'
              type: 'Copy'
              policy: { timeout: '0.00:30:00', retry: 1, retryIntervalInSeconds: 60 }
              inputs: [ onPremSource ]
              outputs: [ { referenceName: dsAzureTable.name, type: 'DatasetReference', parameters: { schema: 'stg', table: '@item().sink' } } ]
              typeProperties: {
                source: { type: 'SqlServerSource', sqlReaderQuery: { value: 'SELECT * FROM @{item().src}', type: 'Expression' } }
                sink: { type: 'AzureSqlSink', preCopyScript: { value: 'TRUNCATE TABLE stg.@{item().sink}', type: 'Expression' }, writeBehavior: 'insert' }
              }
            }
          ]
        }
      }
      {
        name: 'CP_FactsIncremental'
        type: 'Copy'
        description: 'Facts inserted or updated by SSIS runs above the cloud watermark and up to the source max.'
        dependsOn: [
          { activity: 'LKP_SourceMaxRun', dependencyConditions: [ 'Succeeded' ] }
          { activity: 'LKP_CopyWatermark', dependencyConditions: [ 'Succeeded' ] }
        ]
        policy: { timeout: '0.01:00:00', retry: 1, retryIntervalInSeconds: 60 }
        inputs: [ onPremSource ]
        outputs: [ { referenceName: dsAzureTable.name, type: 'DatasetReference', parameters: { schema: 'stg', table: 'FactLabResult' } } ]
        typeProperties: {
          source: {
            type: 'SqlServerSource'
            sqlReaderQuery: {
              value: 'SELECT * FROM dw.FactLabResult WHERE (LoadRunId > ${wm} AND LoadRunId <= ${maxRun}) OR (UpdatedRunId > ${wm} AND UpdatedRunId <= ${maxRun})'
              type: 'Expression'
            }
          }
          sink: { type: 'AzureSqlSink', preCopyScript: 'TRUNCATE TABLE stg.FactLabResult', writeBehavior: 'insert' }
        }
      }
      {
        name: 'SP_MergeFromStage'
        type: 'SqlServerStoredProcedure'
        dependsOn: [
          { activity: 'FE_CopyFullTables', dependencyConditions: [ 'Succeeded' ] }
          { activity: 'CP_FactsIncremental', dependencyConditions: [ 'Succeeded' ] }
        ]
        policy: { timeout: '0.00:30:00', retry: 1, retryIntervalInSeconds: 60 }
        linkedServiceName: { referenceName: lsAzure.name, type: 'LinkedServiceReference' }
        typeProperties: {
          storedProcedureName: 'etl.usp_MergeFromStage'
          storedProcedureParameters: {
            PipelineRunId: { value: { value: '@pipeline().RunId', type: 'Expression' }, type: 'String' }
            SourceMaxRunId: { value: { value: '@activity(\'LKP_SourceMaxRun\').output.firstRow.MaxRunId', type: 'Expression' }, type: 'Int32' }
          }
        }
      }
    ]
  }
}

// Deployed stopped; deploy.ps1 -StartTrigger turns it on.
resource trigger 'Microsoft.DataFactory/factories/triggers@2018-06-01' = {
  parent: adf
  name: 'TR_Daily'
  properties: {
    type: 'ScheduleTrigger'
    description: 'Every day after the nightly SSIS load.'
    typeProperties: {
      recurrence: {
        frequency: 'Day'
        interval: 1
        startTime: '2026-10-05T00:00:00'
        timeZone: 'Eastern Standard Time'
        schedule: { hours: [ triggerHour ], minutes: [ 0 ] }
      }
    }
    pipelines: [ { pipelineReference: { referenceName: pipeline.name, type: 'PipelineReference' } } ]
  }
}

output factoryName string = adf.name
output factoryPrincipalId string = adf.identity.principalId
output sqlServerFqdn string = '${sqlServer.name}${environment().suffixes.sqlServerHostname}'
output integrationRuntimeName string = shir.name
