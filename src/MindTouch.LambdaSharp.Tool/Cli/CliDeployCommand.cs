/*
 * MindTouch λ#
 * Copyright (C) 2006-2018 MindTouch, Inc.
 * www.mindtouch.com  oss@mindtouch.com
 *
 * For community documentation and downloads visit mindtouch.com;
 * please review the licensing section.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Amazon.S3.Model;
using Humidifier.Json;
using McMaster.Extensions.CommandLineUtils;
using MindTouch.LambdaSharp.Tool.Internal;

namespace MindTouch.LambdaSharp.Tool.Cli {

    public class CliDeployCommand : ACliCommand {

        //--- Class Methods ---
        public void Register(CommandLineApplication app) {
            app.Command("deploy", cmd => {
                cmd.HelpOption();
                cmd.Description = "Deploy LambdaSharp module";
                var dryRunOption = cmd.Option("--dryrun:<LEVEL>", "(optional) Generate output assets without deploying (0=everything, 1=cloudformation)", CommandOptionType.SingleOrNoValue);
                var outputCloudFormationFilePathOption = cmd.Option("--output <FILE>", "(optional) Name of generated CloudFormation template file (default: bin/cloudformation.json)", CommandOptionType.SingleValue);
                var allowDataLossOption = cmd.Option("--allow-data-loss", "(optional) Allow CloudFormation resource update operations that could lead to data loss", CommandOptionType.NoValue);
                var protectStackOption = cmd.Option("--protect", "(optional) Enable termination protection for the CloudFormation stack", CommandOptionType.NoValue);
                var initSettingsCallback = CreateSettingsInitializer(cmd);
                cmd.OnExecute(async () => {
                    Console.WriteLine($"{app.FullName} - {cmd.Description}");

                    // read settings and validate them
                    var settingsCollection = await initSettingsCallback();
                    if(settingsCollection == null) {
                        return;
                    }
                    foreach(var settings in settingsCollection) {
                        if(settings.IsLocalModule && !File.Exists(settings.ModuleSource)) {
                            AddError($"could not find '{settings.ModuleSource}'");
                        }
                    }
                    if(ErrorCount > 0) {
                        return;
                    }
                    DryRunLevel? dryRun = null;
                    if(dryRunOption.HasValue()) {
                        DryRunLevel value;
                        if(!TryParseEnumOption(dryRunOption, DryRunLevel.Everything, out value)) {

                            // NOTE (2018-08-04, bjorg): no need to add an error message since it's already added by `TryParseEnumOption`
                            return;
                        }
                        dryRun = value;
                    }
                    Console.WriteLine($"Readying module for deployment tier '{settingsCollection.First().Tier}'");
                    foreach(var settings in settingsCollection) {
                        if(!await Deploy(
                            settings,
                            dryRun,
                            outputCloudFormationFilePathOption.Value() ?? Path.Combine(settings.OutputDirectory, "cloudformation.json"),
                            allowDataLossOption.HasValue(),
                            protectStackOption.HasValue()
                        )) {
                            break;
                        }
                    }
                });
            });
        }

        private async Task<bool> Deploy(
            Settings settings,
            DryRunLevel? dryRun,
            string outputCloudFormationFilePath,
            bool allowDataLoos,
            bool protectStack
        ) {
            var stopwatch = Stopwatch.StartNew();

            // check that LambdaSharp Environment & Tool versions match
            if(settings.EnvironmentVersion == null) {
                AddError("could not determine the LambdaSharp Environment version", new LambdaSharpDeploymentTierSetupException(settings.Tier));
            } else {
                if(settings.EnvironmentVersion != settings.ToolVersion) {
                    AddError($"LambdaSharp Tool (v{settings.ToolVersion}) and Environment (v{settings.EnvironmentVersion}) versions do not match", new LambdaSharpDeploymentTierSetupException(settings.Tier));
                }
            }

            // read input file
            Console.WriteLine();
            Console.WriteLine($"Processing module: {settings.ModuleSource}");
            string source;
            if(settings.IsLocalModule) {
                source = await File.ReadAllTextAsync(settings.ModuleSource);
            } else {
                var uri = new Uri(settings.ModuleSource);
                if(uri.Scheme == "s3") {
                    var s3 = settings.ModuleSource.ToS3Info();
                    var response = await settings.S3Client.GetObjectAsync(new GetObjectRequest {
                        BucketName = s3.Bucket,
                        Key = s3.Key
                    });
                    using(var reader = new StreamReader(response.ResponseStream, Encoding.UTF8)) {
                        source = await reader.ReadToEndAsync();
                    }
                } else {
                    using(var httpClient = new HttpClient()) {
                        using(var res = await httpClient.GetAsync(uri))
                        using(var content = res.Content) {
                            source = await content.ReadAsStringAsync();
                        }
                    }
                }
            }

            // preprocess file
            var tokenStream = new ModelPreprocessor(settings).Preprocess(source);
            if(ErrorCount > 0) {
                return false;
            }

            // parse yaml module file
            var module = new ModelParser(settings).Process(tokenStream);
            if(ErrorCount > 0) {
                return false;
            }

            // reset settings when the 'LambdaSharp` module is being deployed
            if(module.Name == "LambdaSharp") {
                settings.Reset();
            }

            // validate module
            new ModelValidation(settings).Process(module);
            if(ErrorCount > 0) {
                return false;
            }

            // packaging assets only applies to local modules
            if(settings.IsLocalModule) {

                // package all functions
                new ModelFunctionPackager(settings).Process(
                    module,
                    skipCompile: dryRun == DryRunLevel.CloudFormation
                );
                if(ErrorCount > 0) {
                    return false;
                }

                // package all files
                new ModelFilesPackager(settings).Process(module);
                if(ErrorCount > 0) {
                    return false;
                }

                // check if assets need to be uploaded
                if(module.Functions.Any() || module.Parameters.Any(p => p.Package != null)) {

                    // check if a deployment bucket was specified
                    if(settings.DeploymentBucketName == null) {
                        AddError("deploying functions requires a deployment bucket", new LambdaSharpDeploymentTierSetupException(settings.Tier));
                        return false;
                    }
                }
                await new ModelUploader(settings).ProcessAsync(
                    module,
                    settings.DeploymentBucketName,
                    skipUpload: dryRun == DryRunLevel.CloudFormation,
                    publish: false,
                    forceUpdate: false
                );
            } else {

                // TODO (2018-08-23, bjorg): make sure all functions/packages have S3 locations
            }

            // resolve all imported parameters
            new ModelImportProcessor(settings).Process(module);
            if(ErrorCount > 0) {
                return false;
            }

            // compile module file
            var moduleObject = new ModelConverter(settings).Process(module);
            if(ErrorCount > 0) {
                return false;
            }

            // generate cloudformation template
            var stack = new ModelGenerator(settings).Generate(moduleObject);
            if(ErrorCount > 0) {
                return false;
            }

            // serialize stack to disk
            var result = true;
            var template = new JsonStackSerializer().Serialize(stack);
            File.WriteAllText(outputCloudFormationFilePath, template);
            if(dryRun == null) {
                result = await new StackUpdater().Deploy(moduleObject, outputCloudFormationFilePath, allowDataLoos, protectStack);
                if(settings.OutputDirectory == settings.WorkingDirectory) {
                    try {
                        File.Delete(outputCloudFormationFilePath);
                    } catch { }
                }
            }
            Console.WriteLine($"Done (duration: {stopwatch.Elapsed:c})");
            return result;
        }
    }
}
