// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using DatabaseSchemaReader;

namespace Microsoft.DbContextPackage.Handlers
{
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Data.Common;
    using System.Data.Entity.Design;
    using System.Data.Entity.Design.PluralizationServices;
    using System.Data.Metadata.Edm;
    using System.Data.SqlClient;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Xml;
    using Microsoft.DbContextPackage.Extensions;
    using Microsoft.DbContextPackage.Resources;
    using Microsoft.DbContextPackage.Utilities;
    using Microsoft.VisualStudio.Data.Core;
    using Microsoft.VisualStudio.Data.Services;
    using Microsoft.VisualStudio.Shell;
    using Project = EnvDTE.Project;

    /// <summary>
    /// 
    /// </summary>
    internal class ReverseEngineerCodeFirstHandler
    {
        private static readonly IEnumerable<EntityStoreSchemaFilterEntry> _storeMetadataFilters = new[]
            {
                new EntityStoreSchemaFilterEntry(null, null, "EdmMetadata", EntityStoreSchemaFilterObjectTypes.Table, EntityStoreSchemaFilterEffect.Exclude),
                new EntityStoreSchemaFilterEntry(null, null, "__MigrationHistory", EntityStoreSchemaFilterObjectTypes.Table, EntityStoreSchemaFilterEffect.Exclude)
            };
        private readonly DbContextPackage _package;

        public ReverseEngineerCodeFirstHandler(DbContextPackage package)
        {
            DebugCheck.NotNull(package);

            _package = package;
        }

        public void ReverseEngineerCodeFirst(Project project)
        {
            DebugCheck.NotNull(project);

            try
            {
                var startTime = DateTime.Now;

                // Show dialog with SqlClient selected by default 打开SqlClient选择窗口
                var dialogFactory = _package.GetService<IVsDataConnectionDialogFactory>();
                var dialog = dialogFactory.CreateConnectionDialog();
                dialog.AddAllSources();
                dialog.SelectedSource = new Guid("067ea0d9-ba62-43f7-9106-34930c60c528");
                var dialogResult = dialog.ShowDialog(connect: true);

                if (dialogResult != null)
                {
                    // Find connection string and provider 从用户操作中获取ConnectionString
                    _package.DTE2.StatusBar.Text = Strings.ReverseEngineer_LoadSchema;
                    var connection = (DbConnection)dialogResult.GetLockedProviderObject();
                    var connectionString = connection.ConnectionString;
                    var providerManager = (IVsDataProviderManager)Package.GetGlobalService(typeof(IVsDataProviderManager));
                    IVsDataProvider dp;
                    providerManager.Providers.TryGetValue(dialogResult.Provider, out dp);
                    var providerInvariant = (string)dp.GetProperty("InvariantName");

                    //加载符号,方便后面得到表和字段注释 ,此功能以避免原来模型中无法读取注释功能
                    var documentDescription = new DocumentDescription(providerInvariant, connectionString);

                    // Load store schema 加载数据库
                    var storeGenerator = new EntityStoreSchemaGenerator(providerInvariant, connectionString, "dbo");
                    storeGenerator.GenerateForeignKeyProperties = true;
                    var errors = storeGenerator.GenerateStoreMetadata(_storeMetadataFilters).Where(e => e.Severity == EdmSchemaErrorSeverity.Error);
                    errors.HandleErrors(Strings.ReverseEngineer_SchemaError);

                    // Generate default mapping
                    _package.DTE2.StatusBar.Text = Strings.ReverseEngineer_GenerateMapping;
                    //设置上下文名称 
                    //TODO 需要把上下文修改为 PASCAL 命名法
                    var contextName = CharacterPartitionUtil.ConvertPascalCharacter(connection.Database).Replace(" ", string.Empty).Replace(".", string.Empty) + "Context";
                    var modelGenerator = new EntityModelSchemaGenerator(storeGenerator.EntityContainer, "DefaultNamespace", contextName);
                    modelGenerator.PluralizationService = PluralizationService.CreateService(new CultureInfo("en"));
                    modelGenerator.GenerateForeignKeyProperties = true;
                    modelGenerator.GenerateMetadata();

                    // Pull out info about types to be generated 获取所有实体集合
                    var entityTypes = modelGenerator.EdmItemCollection.OfType<EntityType>().ToArray();
                    var mappings = new EdmMapping(modelGenerator, storeGenerator.StoreItemCollection);

                    // Find the project to add the code to 获取文件需要附加的项目
                    var vsProject = (VSLangProj.VSProject)project.Object;
                    var projectDirectory = new FileInfo(project.FileName).Directory;
                    var projectNamespace = (string)project.Properties.Item("RootNamespace").Value;
                    var references = vsProject.References.Cast<VSLangProj.Reference>();

                    //对引用进行处理
                    if (!references.Any(r => r.Name == "EntityFramework"))
                    {
                        // Add EF References
                        _package.DTE2.StatusBar.Text = Strings.ReverseEngineer_InstallEntityFramework;

                        try
                        {
                            project.InstallPackage("EntityFramework");
                        }
                        catch (Exception ex)
                        {
                            _package.LogError(Strings.ReverseEngineer_InstallEntityFrameworkError, ex);
                        }
                    }

                    #region 生成实体代码和映射实体类
                    // Generate Entity Classes and Mappings
                    var templateProcessor = new TemplateProcessor(project);
                    var modelsNamespace = projectNamespace + ".Models";//获得模型的命名空间
                    var modelsDirectory = Path.Combine(projectDirectory.FullName, "Models");//模型路径
                    var mappingNamespace = modelsNamespace + ".Mapping";//映射的命名空间
                    var mappingDirectory = Path.Combine(modelsDirectory, "Mapping");//映射路径
                    var entityFrameworkVersion = GetEntityFrameworkVersion(references);

                    //所有实体都已经存在于该集合中,从数据库中获取的实体名称
                    foreach (var entityType in entityTypes)
                    {
                        _package.DTE2.StatusBar.Text = Strings.ReverseEngineer_GenerateClasses(entityType.Name);

                        #region 处理实体命名
                        // Generate the code file 
                        //TODO 这里需要处理实体的(即表名)命名方式

                        var entityHost = new EfTextTemplateHost
                            {
                                EntityPascalName = CharacterPartitionUtil.ConvertPascalMany(entityType.Name),
                                DocumentDictionary = documentDescription.GetDocumentEntity(entityType.Name).TableFieldsKeyPair,
                                DocumentDescription = documentDescription.GetDocumentDescription(entityType.Name),
                                EntityDescription = GetDocumentDescription(entityType.Documentation),
                                EntityType = entityType,
                                EntityContainer = modelGenerator.EntityContainer,
                                Namespace = modelsNamespace,
                                ModelsNamespace = modelsNamespace,
                                MappingNamespace = mappingNamespace,
                                EntityFrameworkVersion = entityFrameworkVersion,
                                TableSet = mappings.EntityMappings[entityType].Item1,
                                PropertyToColumnMappings = mappings.EntityMappings[entityType].Item2,
                                ManyToManyMappings = mappings.ManyToManyMappings
                            };
                        //给T4 引擎传递实体 ,并且在这里处理Template的内容
                        var entityContents = templateProcessor.Process(Templates.EntityTemplate, entityHost);

                        //TODO 这里也需要处理物理文件的命名方式
                        var filePath = Path.Combine(modelsDirectory, CharacterPartitionUtil.ConvertPascalMany(entityType.Name) + entityHost.FileExtension);
                        project.AddNewFile(filePath, entityContents);

                        #endregion

                        #region 处理映射实体命名
                        var mappingHost = new EfTextTemplateHost
                            {
                                EntityPascalName = CharacterPartitionUtil.ConvertPascalMany(entityType.Name),
                                EntityMaperPascalName = CharacterPartitionUtil.ConvertPascalMany(entityType.Name) + "Map",
                                EntityType = entityType,
                                EntityContainer = modelGenerator.EntityContainer,
                                Namespace = mappingNamespace,
                                ModelsNamespace = modelsNamespace,
                                MappingNamespace = mappingNamespace,
                                EntityFrameworkVersion = entityFrameworkVersion,
                                TableSet = mappings.EntityMappings[entityType].Item1,
                                PropertyToColumnMappings = mappings.EntityMappings[entityType].Item2,
                                ManyToManyMappings = mappings.ManyToManyMappings
                            };
                        var mappingContents = templateProcessor.Process(Templates.MappingTemplate, mappingHost);

                        var mappingFilePath = Path.Combine(mappingDirectory, CharacterPartitionUtil.ConvertPascalMany(entityType.Name) + "Map" + mappingHost.FileExtension);
                        project.AddNewFile(mappingFilePath, mappingContents);
                        #endregion
                    }

                    #region 处理上下文实体环境
                    List<Tuple<string,string>>   entitiesDictionary = new List<Tuple<string,string>>();
                    foreach (var typeEntity in modelGenerator.EntityContainer.BaseEntitySets.OfType<EntitySet>())
                    {
                        entitiesDictionary.Add(new Tuple<string, string>(CharacterPartitionUtil.ConvertPascalMany(typeEntity.ElementType.Name), CharacterPartitionUtil.ConvertPascalMany(typeEntity.Name)));
                    }

                    // Generate Context
                    _package.DTE2.StatusBar.Text = Strings.ReverseEngineer_GenerateContext;
                    var contextHost = new EfTextTemplateHost
                        {
                            EntitiesDictionary = entitiesDictionary,
                            EntityContainer = modelGenerator.EntityContainer,
                            Namespace = modelsNamespace,
                            ModelsNamespace = modelsNamespace,
                            MappingNamespace = mappingNamespace,
                            EntityFrameworkVersion = entityFrameworkVersion
                        };
                    var contextContents = templateProcessor.Process(Templates.ContextTemplate, contextHost);

                    var contextFilePath = Path.Combine(modelsDirectory, modelGenerator.EntityContainer.Name + contextHost.FileExtension);
                    var contextItem = project.AddNewFile(contextFilePath, contextContents);
                    #endregion

                    //把SQL链接字符串添加到配置文件
                    AddConnectionStringToConfigFile(project, connectionString, providerInvariant, modelGenerator.EntityContainer.Name);

                    if (contextItem != null)
                    {
                        // Open context class when done
                        _package.DTE2.ItemOperations.OpenFile(contextFilePath);
                    }

                    var duration = DateTime.Now - startTime;
                    _package.DTE2.StatusBar.Text = Strings.ReverseEngineer_Complete(duration.ToString(@"h\:mm\:ss"));
                    #endregion
                }
            }
            catch (Exception exception)
            {
                _package.LogError(Strings.ReverseEngineer_Error, exception);
            }
        }

        private static string GetDocumentDescription(Documentation  documentation)
        {
            if (null != documentation && !documentation.IsEmpty)
            {
                if (!String.IsNullOrEmpty(documentation.LongDescription))
                {
                    return documentation.LongDescription;
                }
                else
                {
                    if (!String.IsNullOrEmpty(documentation.Summary))
                    {
                        return documentation.Summary;
                    }
                }
            }
            return "EmptyDescription";
        }

        private static Version GetEntityFrameworkVersion(IEnumerable<VSLangProj.Reference> references)
        {
            var entityFrameworkReference = references.FirstOrDefault(r => r.Name == "EntityFramework");

            if (entityFrameworkReference != null)
            {
                return new Version(entityFrameworkReference.Version);
            }

            return null;
        }

        private static void AddConnectionStringToConfigFile(Project project, string connectionString, string providerInvariant, string connectionStringName)
        {
            DebugCheck.NotNull(project);
            DebugCheck.NotEmpty(providerInvariant);
            DebugCheck.NotEmpty(connectionStringName);

            // Find App.config or Web.config
            var configFilePath = Path.Combine(
                project.GetProjectDir(),
                project.IsWebProject()
                    ? "Web.config"
                    : "App.config");

            // Either load up the existing file or create a blank file
            var config = ConfigurationManager.OpenMappedExeConfiguration(
                new ExeConfigurationFileMap { ExeConfigFilename = configFilePath },
                ConfigurationUserLevel.None);

            // Find or create the connectionStrings section
            var connectionStringSettings = config.ConnectionStrings
                .ConnectionStrings
                .Cast<ConnectionStringSettings>()
                .FirstOrDefault(css => css.Name == connectionStringName);

            if (connectionStringSettings == null)
            {
                connectionStringSettings = new ConnectionStringSettings
                    {
                        Name = connectionStringName
                    };

                config.ConnectionStrings
                    .ConnectionStrings
                    .Add(connectionStringSettings);
            }

            // Add in the new connection string
            connectionStringSettings.ProviderName = providerInvariant;
            connectionStringSettings.ConnectionString = FixUpConnectionString(connectionString, providerInvariant);

            project.DTE.SourceControl.CheckOutItemIfNeeded(configFilePath);
            config.Save();

            // Add any new file to the project
            project.ProjectItems.AddFromFile(configFilePath);
        }

        private static string FixUpConnectionString(string connectionString, string providerName)
        {
            DebugCheck.NotEmpty(providerName);

            if (providerName != "System.Data.SqlClient")
            {
                return connectionString;
            }

            var builder = new SqlConnectionStringBuilder(connectionString)
                {
                    MultipleActiveResultSets = true
                };
            builder.Remove("Pooling");

            return builder.ToString();
        }

        private class EdmMapping
        {
            public EdmMapping(EntityModelSchemaGenerator mcGenerator, StoreItemCollection store)
            {
                DebugCheck.NotNull(mcGenerator);
                DebugCheck.NotNull(store);

                // Pull mapping xml out
                var mappingDoc = new XmlDocument();
                var mappingXml = new StringBuilder();

                using (var textWriter = new StringWriter(mappingXml))
                {
                    mcGenerator.WriteStorageMapping(new XmlTextWriter(textWriter));
                }

                mappingDoc.LoadXml(mappingXml.ToString());

                var entitySets = mcGenerator.EntityContainer.BaseEntitySets.OfType<EntitySet>();
                var associationSets = mcGenerator.EntityContainer.BaseEntitySets.OfType<AssociationSet>();
                var tableSets = store.GetItems<EntityContainer>().Single().BaseEntitySets.OfType<EntitySet>();

                this.EntityMappings = BuildEntityMappings(mappingDoc, entitySets, tableSets);
                this.ManyToManyMappings = BuildManyToManyMappings(mappingDoc, associationSets, tableSets);
            }

            public Dictionary<EntityType, Tuple<EntitySet, Dictionary<EdmProperty, EdmProperty>>> EntityMappings { get; set; }

            public Dictionary<AssociationType, Tuple<EntitySet, Dictionary<RelationshipEndMember, Dictionary<EdmMember, string>>>> ManyToManyMappings { get; set; }

            private static Dictionary<AssociationType, Tuple<EntitySet, Dictionary<RelationshipEndMember, Dictionary<EdmMember, string>>>> BuildManyToManyMappings(XmlDocument mappingDoc, IEnumerable<AssociationSet> associationSets, IEnumerable<EntitySet> tableSets)
            {
                DebugCheck.NotNull(mappingDoc);
                DebugCheck.NotNull(associationSets);
                DebugCheck.NotNull(tableSets);

                // Build mapping for each association
                var mappings = new Dictionary<AssociationType, Tuple<EntitySet, Dictionary<RelationshipEndMember, Dictionary<EdmMember, string>>>>();
                var namespaceManager = new XmlNamespaceManager(mappingDoc.NameTable);
                namespaceManager.AddNamespace("ef", mappingDoc.ChildNodes[0].NamespaceURI);
                foreach (var associationSet in associationSets.Where(a => !a.ElementType.AssociationEndMembers.Where(e => e.RelationshipMultiplicity != RelationshipMultiplicity.Many).Any()))
                {
                    var setMapping = mappingDoc.SelectSingleNode(string.Format("//ef:AssociationSetMapping[@Name=\"{0}\"]", associationSet.Name), namespaceManager);
                    var tableName = setMapping.Attributes["StoreEntitySet"].Value;
                    var tableSet = tableSets.Single(s => s.Name == tableName);

                    var endMappings = new Dictionary<RelationshipEndMember, Dictionary<EdmMember, string>>();
                    foreach (var end in associationSet.AssociationSetEnds)
                    {
                        var propertyToColumnMappings = new Dictionary<EdmMember, string>();
                        var endMapping = setMapping.SelectSingleNode(string.Format("./ef:EndProperty[@Name=\"{0}\"]", end.Name), namespaceManager);
                        foreach (XmlNode fk in endMapping.ChildNodes)
                        {
                            var propertyName = fk.Attributes["Name"].Value;
                            var property = end.EntitySet.ElementType.Properties[propertyName];
                            var columnName = fk.Attributes["ColumnName"].Value;
                            propertyToColumnMappings.Add(property, columnName);
                        }

                        endMappings.Add(end.CorrespondingAssociationEndMember, propertyToColumnMappings);
                    }

                    mappings.Add(associationSet.ElementType, Tuple.Create(tableSet, endMappings));
                }

                return mappings;
            }

            private static Dictionary<EntityType, Tuple<EntitySet, Dictionary<EdmProperty, EdmProperty>>> BuildEntityMappings(XmlDocument mappingDoc, IEnumerable<EntitySet> entitySets, IEnumerable<EntitySet> tableSets)
            {
                DebugCheck.NotNull(mappingDoc);
                DebugCheck.NotNull(entitySets);
                DebugCheck.NotNull(tableSets);

                // Build mapping for each type
                var mappings = new Dictionary<EntityType, Tuple<EntitySet, Dictionary<EdmProperty, EdmProperty>>>();
                var namespaceManager = new XmlNamespaceManager(mappingDoc.NameTable);
                namespaceManager.AddNamespace("ef", mappingDoc.ChildNodes[0].NamespaceURI);
                foreach (var entitySet in entitySets)
                {
                    // Post VS2010 builds use a different structure for mapping
                    var setMapping = mappingDoc.ChildNodes[0].NamespaceURI == "http://schemas.microsoft.com/ado/2009/11/mapping/cs"
                        ? mappingDoc.SelectSingleNode(string.Format("//ef:EntitySetMapping[@Name=\"{0}\"]/ef:EntityTypeMapping/ef:MappingFragment", entitySet.Name), namespaceManager)
                        : mappingDoc.SelectSingleNode(string.Format("//ef:EntitySetMapping[@Name=\"{0}\"]", entitySet.Name), namespaceManager);

                    var tableName = setMapping.Attributes["StoreEntitySet"].Value;
                    var tableSet = tableSets.Single(s => s.Name == tableName);

                    var propertyMappings = new Dictionary<EdmProperty, EdmProperty>();
                    foreach (var prop in entitySet.ElementType.Properties)
                    {
                        var propMapping = setMapping.SelectSingleNode(string.Format("./ef:ScalarProperty[@Name=\"{0}\"]", prop.Name), namespaceManager);
                        var columnName = propMapping.Attributes["ColumnName"].Value;
                        var columnProp = tableSet.ElementType.Properties[columnName];

                        propertyMappings.Add(prop, columnProp);
                    }

                    mappings.Add(entitySet.ElementType, Tuple.Create(tableSet, propertyMappings));
                }

                return mappings;
            }
        }
    }

    internal class DocumentDescription
    {
        private string _providerName;
        private string _connectionString;

        private Dictionary<string, TableDocument> _descriptionDictionary = new Dictionary<string, TableDocument>();

        public DocumentDescription(string providerName, string connectionString)
        {
            this._providerName = providerName;
            this._connectionString = connectionString;
            GetDescriptionDocument();
        }

        public string GetDocumentDescription(string tableName)
        {
            var documentEntity = GetDocumentEntity(tableName);
            if (documentEntity != null)
            {
                return documentEntity.TableDescription;
            }
            return String.Empty;
        }

        public TableDocument GetDocumentEntity(string tableName)
        {
            if (_descriptionDictionary.ContainsKey(tableName.ToLower()))
            {
                TableDocument tableDocument;
                _descriptionDictionary.TryGetValue(tableName.ToLower(), out tableDocument);
                return tableDocument;
            }
            return null;
        }

        public string GetColumnDescription(string tableName,string columnName)
        {
            var documentEntity = GetDocumentEntity(tableName);
            if (documentEntity != null)
            {
                if (documentEntity.TableFieldsKeyPair.ContainsKey(columnName.ToLower()))
                {
                    return documentEntity.TableFieldsKeyPair[columnName.ToLower()];
                }
            }
            return String.Empty;
        }

        private void GetDescriptionDocument()
        {
            var dbReader = new DatabaseReader(_connectionString, _providerName);
            var schema = dbReader.ReadAll();
            foreach (var table in schema.Tables)
            {
                var tableDocument = new TableDocument(table.Name.ToLower(), table.Description);
                _descriptionDictionary.Add(table.Name.ToLower(), tableDocument);
                foreach (var column in table.Columns)
                {
                    tableDocument.AddField(column.Name.ToLower(), column.Description);
                }
            }
        }
    }

    internal class TableDocument
    {
        public TableDocument(string tableName, string tableDescription)
        {
            this.TableName = tableName;
            this.TableDescription = tableDescription;
        }

        public void AddField(string name, string description)
        {
            this._tableFields.Add(name, description);
        }

        public string TableName { get; protected set; }

        public string TableDescription { get; protected set; }

        private Dictionary<string, string> _tableFields = new Dictionary<string, string>();

        public IDictionary<string, string> TableFieldsKeyPair { get { return _tableFields; } }
    }

    internal static class CharacterPartitionUtil
    {
        public static string ConvertPascalCharacter(string character)
        {
            if (String.IsNullOrEmpty(character)) return character;
            TextInfo laxer = new CultureInfo("en-US", false).TextInfo;
            return laxer.ToTitleCase(character);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="manyCharacteres">用下划线 "_" 分割</param>
        /// <returns>Pascal字符</returns>
        public static string ConvertPascalMany(string manyCharacteres)
        {
            if (String.IsNullOrEmpty(manyCharacteres)) return manyCharacteres;
            string[] charArray = manyCharacteres.Split('_');
            return charArray.Aggregate("", (current, s) => current + ConvertPascalCharacter(s));
        }
    }
}
