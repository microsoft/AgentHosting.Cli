# Additional Coding Agent Instructions

This document is provided in case the agent.md file has not been downloaded or made available to the coding agent's session. Coding agents should review this document for instructions on using the msagent CLI and should be used in combination with the wider msagent documentation here and the CLI's help menu.

## Do not use the PAC CLI together with msagent

- The PAC CLI is the power platform CLI and is the older version of the CLI made to interact with copilot studio. Whilst it is fully supported, using it together with msagent CLI can cause unexpected issues and conflicts, as they work differently. Never combine the two CLI's when working with copilot studio within a local folder directory.

## CodeAgent is not supported

- You may see placeholders in the CLI such as `codeagent` which is a different type and is not yet supported.

## When testing locally

- Clarify with the user how they want to test. Testing can be done with human interactions using the agent playground local UX application or coding agents can use one or more sessions to run tests interacting to copilot studio runtime from their local machine.

## Authenticating with copilot studio when testing locally

- The CLI brokers the authentication between the local machine and accessing Copilot Studio, which must be using integrated auth or manual authentication using Azure Entra Active Directory v2 when using non-interactive or copilot studio client with agent playground. If using DirectLine with the agent playground it uses DirectLine tokens which must be obtained from the Copilot Studio UI under the settings area. The CLI user will be required to give the CLI permissions to access Copilot Studio on their behalf, and this will be needed to be granted in the browser by the person giving consent. This should only be needed once and refreshed periodically.

## Workflows and disambiguating the different types

- There are different types of agent workflow available based on the different versions of both copilot studio (standard harness) and workflows (cloud flows, power automate, different designers). There are some differences between the definitions of these workflows (that use json) that determine how they are deployed to the runtime that is very important to understand. Ensure you read the reference material which covers these differences.

## Workflows standalone and workflows connected to an agent

- Workflows are created and can be referenced by an agent as a tool, or in the new preview feature for hooks. Workflows can also be used standalone, without an agent. 

[Guidance coming soon on what to look for if these types of workflows and how they are portable and referenceable or should be kept separate]

## Understand the different types of copilot studio harness 

- There are two types of copilot studio agent, one which is a standard harness agent and one which is a github copilot harness agent. They are defined by their type in the Dataverse schema definition [here](comingsoon). Features and functionality are different based on copilot studio types, and the msagent cli should only be used with github copilot harness agents. For working with standard harness agents, it is recommended to use the PAC CLI, including for scenarios of migration from standard to github copilot harness.
