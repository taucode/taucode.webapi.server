﻿using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TauCode.Cqrs.Commands;
using TauCode.Validation;

namespace TauCode.WebApi.Server
{
    public class ValidationFilterAttribute : ActionFilterAttribute
    {
        private readonly Dictionary<Type, Type> _validatorTypes; // key is validated type (e.g. FooCommand), value is validator (e.g. FooCommandValidator)

        public ValidationFilterAttribute(Assembly coreAssembly)
        {
            if (coreAssembly == null)
            {
                throw new ArgumentNullException(nameof(coreAssembly));
            }

            _validatorTypes = coreAssembly
                .GetTypes()
                .Where(IsCommandValidator)
                .ToDictionary(GetValidatedType, x => x);
        }

        private static Type GetValidatedType(Type validatorType)
        {
            return validatorType.BaseType.GetGenericArguments().Single();
        }

        private static bool IsCommandValidator(Type type)
        {
            var baseType = type.BaseType;

            if (baseType == null)
            {
                return false;
            }

            if (baseType.IsGenericType)
            {
                var generic = baseType.GetGenericTypeDefinition();
                if (generic == typeof(AbstractValidator<>))
                {
                    var arg = baseType.GetGenericArguments().Single();
                    var argInterfaces = arg.GetInterfaces();
                    if (!argInterfaces.Contains(typeof(ICommand)))
                    {
                        return false;
                    }

                    return true;
                }

                return false;
            }

            return false;
        }

        public override void OnActionExecuting(ActionExecutingContext actionContext)
        {
            ValidationErrorDto validationError = null;

            // Verify that the model state is valid before running the argument value specific validators.
            if (!actionContext.ModelState.IsValid)
            {
                // Prepare the validation error response
                validationError = ValidationErrorDto.CreateStandard();

                foreach (var fieldState in actionContext.ModelState)
                {
                    // Make sure that all the property names are camel cased and remove all model prefixes
                    var fieldName = WebApiHostHelper.EnsurePropertyNameIsCamelCase(
                        Regex.Replace(fieldState.Key, @"^(.*?\.)(.*)", "$2"));

                    // Get only the first error, the rest will be skipped
                    var error = fieldState.Value.Errors.First();

                    // Create the error message
                    var errorMessage = "Unknown error.";
                    if (!string.IsNullOrEmpty(error.ErrorMessage))
                    {
                        errorMessage = error.ErrorMessage;
                    }
                    else if (!string.IsNullOrEmpty(error.Exception?.Message))
                    {
                        errorMessage = error.Exception.Message;
                    }

                    // Add the error to the response, with an empty error code, since this is an unspecific error
                    validationError.AddFailure(fieldName, null, errorMessage);
                }
            }

            // Validate all the arguments for the current action
            foreach (var argument in actionContext.ActionDescriptor./*GetParameters()*/Parameters)
            {
                // Skip all arguments without a registered validator
                _validatorTypes.TryGetValue(argument.ParameterType, out var validatorType);
                if (validatorType == null)
                {
                    continue;
                }

                // Get the registered validator
                var validator = (IValidator)actionContext.HttpContext.RequestServices.GetService(validatorType);

                if (validator == null)
                {
                    continue; // could not resolve validator
                }

                // Inject the action arguments into the validator, so that they can be used in the validation
                // This is a "hack" to, amongst other, support unique validation on the update commands where the resource id is needed to exclude itself from the unique check.
                if (validator is IParameterValidator parameterValidator)
                {
                    parameterValidator.Parameters = actionContext.ActionArguments;
                }

                // Validate the argument
                var argumentValue = actionContext.ActionArguments[argument.Name];

                if (argumentValue == null)
                {
                    validationError = ValidationErrorDto.CreateStandard($"Argument '{argument.Name}' is null");
                    break;
                }

                var method = validator.GetType().GetMethod("Validate", new Type[] { argumentValue.GetType() });
                if (method == null)
                {
                    throw new NotImplementedException(); // todo
                }

                //var validationResult = validator.Validate(argumentValue);
                var validationResult = (ValidationResult)method.Invoke(validator, new[] { argumentValue });

                // Return if the argument value was valid
                if (validationResult.IsValid)
                {
                    continue;
                }

                // Create an validation error response, if it does not already exist
                if (validationError == null)
                {
                    validationError = ValidationErrorDto.CreateStandard();
                }

                // Add every field specific error to validation error response
                foreach (var validationFailure in validationResult.Errors)
                {
                    // Make sure that all the property names are camel cased
                    var propertyName = WebApiHostHelper.EnsurePropertyNameIsCamelCase(validationFailure.PropertyName);

                    // Only add the first validation message for a property
                    if (!validationError.Failures.ContainsKey(propertyName))
                    {
                        validationError.AddFailure(propertyName, validationFailure.ErrorCode, validationFailure.ErrorMessage);
                    }
                }
            }

            if (validationError != null)
            {
                actionContext.Result = new ContentResult
                {
                    StatusCode = StatusCodes.Status400BadRequest,
                    ContentType = "application/json",
                    Content = JsonConvert.SerializeObject(validationError),
                };

                // Set the action response to a 400 Bad Request, with the validation error response as content
                actionContext.HttpContext.Response.Headers.Add(DtoHelper.PayloadTypeHeaderName, DtoHelper.ValidationErrorPayloadType);

            }
        }

        public Type[] GetCommandTypes() => _validatorTypes.Keys.ToArray();
    }
}
