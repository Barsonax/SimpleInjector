﻿#region Copyright (c) 2010 S. van Deursen
/* The Simple Injector is an easy-to-use Inversion of Control library for .NET
 * 
 * Copyright (C) 2010 S. van Deursen
 * 
 * To contact me, please visit my blog at http://www.cuttingedge.it/blogs/steven/ or mail to steven at 
 * cuttingedge.it.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
 * associated documentation files (the "Software"), to deal in the Software without restriction, including 
 * without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
 * copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the 
 * following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all copies or substantial 
 * portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT 
 * LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO 
 * EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER 
 * IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE 
 * USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
#endregion

namespace SimpleInjector
{
    using System;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Linq.Expressions;
    using SimpleInjector.Diagnostics;

    /// <summary>Produces instances for a given registration.</summary>
    [DebuggerTypeProxy(typeof(InstanceProducerDebugView))]
    [DebuggerDisplay(
        "ServiceType = {SimpleInjector.Helpers.ToFriendlyName(ServiceType),nq}, " +
        "Lifestyle = {Lifestyle.Name,nq}")]
    public sealed class InstanceProducer
    {
        private readonly Registration registration;
        private CyclicDependencyValidator validator;
        private Func<object> instanceCreator;
        private Lazy<Expression> expression;
        private bool? isValid = true;
        private Lifestyle overriddenLifestyle;

        /// <summary>Initializes a new instance of the <see cref="InstanceProducer"/> class.</summary>
        /// <param name="serviceType">The service type for which this instance is created.</param>
        /// <param name="registration">The <see cref="Registration"/>.</param>
        public InstanceProducer(Type serviceType, Registration registration)
        {
            Requires.IsNotNull(serviceType, "serviceType");
            Requires.IsNotNull(registration, "registration");

            this.ServiceType = serviceType;
            this.registration = registration;

            this.validator = new CyclicDependencyValidator(this.ServiceType);

            this.expression = new Lazy<Expression>(
                () => this.BuildExpressionInternal(),
                System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>
        /// Gets the <see cref="Lifestyle"/> for this registration. The returned lifestyle can differ from the
        /// lifestyle that is used during the registration. This can happen for instance when the registration
        /// is changed by an <see cref="Container.ExpressionBuilding">ExpressionBuilding</see> registration or
        /// gets decorated using the
        /// <see cref="SimpleInjector.Extensions.DecoratorExtensions.RegisterDecorator(Container, Type, Type)">RegisterDecorator</see> method.
        /// </summary>
        public Lifestyle Lifestyle
        {
            get { return this.overriddenLifestyle ?? this.registration.Lifestyle; }
        }

        /// <summary>Gets the service type for which this producer produces instances.</summary>
        /// <value>A <see cref="Type"/> instance.</value>
        public Type ServiceType { get; private set; }

        internal Type ImplementationType
        {
            get { return this.registration.ImplementationType ?? this.ServiceType; }
        }

        // Flag that indicates that this type is created by the container (concrete or collection) or resolved
        // using unregistered type resolution.
        internal bool IsContainerAutoRegistered { get; set; }

        // Will only return false when the type is a concrete unregistered type that was automatically added
        // by the container, while the expression can not be generated.
        // Types that are registered upfront are always considered to be valid, while unregistered types must
        // be validated. The reason for this is that we must prevent the container to throw an exception when
        // GetRegistration() is called for an unregistered (concrete) type that can not be resolved.
        internal bool IsValid
        {
            get
            {
                if (this.isValid == null)
                {
                    this.isValid = this.CanBuildExpression();
                }

                return this.isValid.Value;
            }
        }

        /// <summary>Produces an instance.</summary>
        /// <returns>An instance. Will never return null.</returns>
        /// <exception cref="ActivationException">When the instance could not be retrieved or is null.</exception>
        [SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate", Justification =
            "A property is not appropriate, because get instance could possibly be a heavy operation.")]
        public object GetInstance()
        {
            this.validator.CheckForRecursiveCalls();

            object instance;

            try
            {
                if (this.instanceCreator == null)
                {
                    this.instanceCreator = this.BuildInstanceCreator();
                }

                instance = this.instanceCreator();

                this.RemoveValidator();
            }
            catch (Exception ex)
            {
                this.validator.Reset();

                this.ThrowErrorWhileTryingToGetInstanceOfType(ex);

                throw;
            }

            if (instance == null)
            {
                throw new ActivationException(StringResources.DelegateForTypeReturnedNull(this.ServiceType));
            }

            return instance;
        }

        /// <summary>
        /// Builds an expression that expresses the intent to get an instance by the current producer. A call 
        /// to this method locks the container. No new registrations can't be made after a call to this method.
        /// </summary>
        /// <returns>An Expression.</returns>
        public Expression BuildExpression()
        {
            this.validator.CheckForRecursiveCalls();

            try
            {
                var expression = this.expression.Value;

                this.RemoveValidator();

                return expression;
            }
            catch (Exception ex)
            {
                this.validator.Reset();

                this.ThrowErrorWhileTryingToGetInstanceOfType(ex);

                throw;
            }
        }

        internal KnownRelationship[] GetRelationships()
        {
            return this.registration.GetRelationships();
        }

        internal void EnsureTypeWillBeExplicitlyVerified()
        {
            this.isValid = null;
        }

        private Func<object> BuildInstanceCreator()
        {
            // Don't do recursive checks. The GetInstance() already does that.
            var expression = this.expression.Value;

            try
            {
                return Helpers.CompileExpression(this.registration.Container, expression);
            }
            catch (Exception ex)
            {
                string message =
                    StringResources.ErrorWhileBuildingDelegateFromExpression(this.ServiceType, expression, ex);

                throw new ActivationException(message, ex);
            }
        }

        private Expression BuildExpressionInternal()
        {
            // We must lock the container, because not locking could lead to race conditions.
            this.registration.Container.LockContainer();
            
            var expression = this.registration.BuildExpression();

            if (expression == null)
            {
                throw new ActivationException(StringResources.RegistrationReturnedNullFromBuildExpression(
                    this.registration));
            }

            var e = new ExpressionBuiltEventArgs(this.ServiceType, expression);

            e.Lifestyle = this.Lifestyle;

            e.KnownRelationships = new KnownRelationshipCollection(this.registration.GetRelationships().ToList());

            this.registration.Container.OnExpressionBuilt(e);

            this.registration.ReplaceRelationships(e.KnownRelationships);

            this.overriddenLifestyle = e.Lifestyle;

            return e.Expression;
        }

        private void ThrowErrorWhileTryingToGetInstanceOfType(Exception innerException)
        {
            string exceptionMessage = StringResources.DelegateForTypeThrewAnException(this.ServiceType);

            // Prevent wrapping duplicate exceptions.
            if (!innerException.Message.StartsWith(exceptionMessage, StringComparison.OrdinalIgnoreCase))
            {
                throw new ActivationException(exceptionMessage + " " + innerException.Message, innerException);
            }
        }

        // This method will be inlined by the JIT.
        private void RemoveValidator()
        {
            // No recursive calls detected, we can remove the validator to increase performance.
            // We first check for null, because this is faster. Every time we write, the CPU has to send
            // the new value to all the other CPUs. We only nullify the validator while using the GetInstance
            // method, because the BuildExpression will only be called a limited amount of time.
            if (this.validator != null)
            {
                this.validator = null;
            }
        }

        private bool CanBuildExpression()
        {
            try
            {
                // Test if the instance can be made.
                this.BuildExpression();

                return true;
            }
            catch (ActivationException)
            {
                return false;
            }
        }

        internal sealed class InstanceProducerDebugView
        {
            private readonly InstanceProducer instanceProducer;

            internal InstanceProducerDebugView(InstanceProducer instanceProducer)
            {
                this.instanceProducer = instanceProducer;
            }

            public Lifestyle Lifestyle
            {
                get { return this.instanceProducer.Lifestyle; }
            }

            [DebuggerDisplay("{SimpleInjector.Helpers.ToFriendlyName(ServiceType),nq}")]
            public Type ServiceType { get; private set; }

            [DebuggerDisplay("{SimpleInjector.Helpers.ToFriendlyName(ImplementationType),nq}")]
            public Type ImplementationType
            {
                get { return this.instanceProducer.ImplementationType; }
            }
        }
    }
}