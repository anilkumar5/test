using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using AutoMapper.QueryableExtensions;
using MicroNet.Business.Security;
using MicroNet.MMP.Business.CQRS.Core;
using MicroNet.MMP.Business.Services.ServiceInterfaces;
using MicroNet.MMP.Data;
using MicroNet.MMP.Models.BackOffice;
using MicroNet.MMP.TenantSettings;
using MicroNet.MMP.Util.Extensions;
using System.Threading.Tasks;
using StructureMap;
using MicroNet.MMP.EntityChangeBus;

namespace MicroNet.MMP.Business.CQRS.Commands
{
    public class AddEventCommand : EventTransactionCommandHandler, ICommandHandler<AddEventModel, EventCollectionItemModel>
    {
        private readonly IEventService _eventService;
        private readonly IContainer _container;

        public AddEventCommand(
            IMzDB db,
            ICurrentUser user,
            IEventReminderService eventReminderService,
            ITimeService timeService, ITenantSettings tenantSettings,
            IEventService eventService, IContainer container)
            : base(db, user, eventReminderService, timeService, null, tenantSettings)
        {
            _eventService = eventService;
            _container = container;
        }

        public EventCollectionItemModel ExecuteCommand(AddEventModel model)
        {
            Event @event = Add<AddEventModel>(model);

            if (model.CopyFromExistingEvent && model.ExistingEventId.HasValue)
            {
                CopyBaseInformation(@event, model.ExistingEventId.Value);
                CopyImages(@event.EventDetailId, model.ExistingEventId.Value);
                CopyEventRegistrationInfo(@event.EventDetailId, model.ExistingEventId.Value);
                CopyAttendeeRegistrationTypes(@event.EventDetailId, model.ExistingEventId.Value);
                CopyEventDiscounts(@event.EventDetailId, model.ExistingEventId.Value);
                CopySponsorships(@event.EventDetailId, model.ExistingEventId.Value);


                if (model.CopyTasks)
                {
                    CopyEventTasks(@event.EventDetailId, model.ExistingEventId.Value);
                }
                if (model.CopyExhibitors)
                {
                    CopyEventExhibitors(@event.EventDetailId, model.ExistingEventId.Value);
                }

                if (model.CopyExhibitorSetup)
                {
                    CopyEventExhibitorSetup(@event.EventDetailId, model.ExistingEventId.Value);
                }

                _db.SaveChanges();

                //adding CopyAttendees and CopyAttendeeSetup in background task.
                Task.Run(() =>
                {
                    using (var db = GetMzDb())
                    {
                        if (model.CopyAttendees)
                        {
                            CopyEventAttendee(db, @event.EventDetailId, model.ExistingEventId.Value);
                        }
                        if (model.CopyAttendeeSetup)
                        {
                            CopyEventAttendeeSetup(db, @event.EventDetailId, model.ExistingEventId.Value);
                        }

                        db.SaveChanges();
                    }
                });
            }

            return _db.Events
                .Where(e => e.TenantId == _user.TenantId && e.IsDeleted == false && e.EventId == @event.EventId)
                .ProjectTo<EventCollectionItemModel>()
                .Single();
        }

        private void CopyBaseInformation(Event @event, int existingEventId)
        {
            var existingEvent = _db.Events
                .Include(x => x.EventDetail)
                .Include(x => x.EventDetail.CategoryItems)
                .Include(x => x.EventDetail.Calendars)
                .SingleOrDefault(x => x.EventId == existingEventId && !x.IsDeleted);

            if (existingEvent != null)
            {
                @event.PublishDate = existingEvent.PublishDate;

                if (@event.EventDetail != null)
                {
                    @event.EventDetail.AddressId = CopyAddress(existingEvent.EventDetail.AddressId);
                    @event.EventDetail.OrganizationId = existingEvent.EventDetail.OrganizationId;

                    if (existingEvent.EventDetail.CategoryItems.Count > 0)
                    {
                        _db.Entry(@event.EventDetail).Collection(x => x.CategoryItems).Load();

                        foreach (var item in existingEvent.EventDetail.CategoryItems)
                        {
                            if (@event.EventDetail.CategoryItems.Count(x => x.CategoryItemId == item.CategoryItemId) == 0)
                            {
                                var categoryItem = _db.As<DbContext>().AttachOrGetLocal<IMzDB, CategoryItem>(
                                    ctx => ctx.CategoryItems,
                                    new CategoryItem { CategoryItemId = item.CategoryItemId });
                                @event.EventDetail.CategoryItems.Add(categoryItem);
                            }
                        }
                    }

                    if (existingEvent.EventDetail.Calendars.Count > 0)
                    {
                        _db.Entry(@event.EventDetail).Collection(x => x.Calendars).Load();
                        foreach (var item in existingEvent.EventDetail.Calendars)
                        {
                            if (@event.EventDetail.Calendars.Count(x => x.CalendarId == item.CalendarId) == 0)
                            {
                                var calendarItem = _db.As<DbContext>().AttachOrGetLocal<IMzDB, Calendar>(
                                    ctx => ctx.Calendars,
                                    new Calendar { CalendarId = item.CalendarId });
                                @event.EventDetail.Calendars.Add(calendarItem);
                            }
                        }
                    }
                }
            }
        }

        private int? CopyAddress(int? addressId)
        {
            if (addressId != null)
            {
                var existingAddress =
                    _db.Addresses.FirstOrDefault(
                        x => x.AddressId == addressId && !x.IsDeleted && x.TenantId == _user.TenantId);
                if (existingAddress == null)
                    return null;
                var newAddress = new Address
                {
                    Address1 = existingAddress.Address1,
                    Address2 = existingAddress.Address2,
                    AddressTypeId = existingAddress.AddressTypeId,
                    City = existingAddress.City,
                    StateProvince = existingAddress.StateProvince,
                    CountryId = existingAddress.CountryId,
                    CountyId = existingAddress.CountyId,
                    PostalCode = existingAddress.PostalCode
                };
                _db.Addresses.Add(newAddress);
                _db.SaveChanges();

                var id = newAddress.AddressId;

                return id;
            }
            return null;
        }

        private void CopyImages(int eventDetailId, int existingEventId)
        {
            var images = _db.Events.Where(x => x.EventId == existingEventId)
                .SelectMany(x => x.EventDetail.EventImages);

            foreach (var img in images)
            {
                var image = new EventImage
                {
                    EventDetailId = eventDetailId,
                    FileImageInfoId = img.FileImageInfoId,
                    GalleryOrder = img.GalleryOrder
                };

                _db.EventImages.Add(image);
            }
        }

        private void CopySponsorships(int eventDetailId, int existingEventId)
        {
            var sponsorships = _db.Events.Where(x => x.EventId == existingEventId)
                .SelectMany(x => x.EventDetail.EventSponsorships)
                .Include(x => x.EventSponsorshipSaleableItems)
                .Include(x => x.SponsorshipBenefits)
                .Where(x => !x.IsDeleted);

            foreach (var sponsorship in sponsorships)
            {
                var newSponsorship = new EventSponsorship
                {
                    EventDetailId = eventDetailId,
                    Name = sponsorship.Name,
                    Description = sponsorship.Description,
                    Position = sponsorship.Position,
                    SponsorColor = sponsorship.SponsorColor,
                    Price = sponsorship.Price,
                    Quantity = sponsorship.Quantity,
                    SalesGoalQuantity = sponsorship.SalesGoalQuantity,
                    LogoHeight = sponsorship.LogoHeight,
                    LogoWidth = sponsorship.LogoWidth,
                    AllowOnlinePurchase = sponsorship.AllowOnlinePurchase,
                    AllowOnlineInvoice = sponsorship.AllowOnlineInvoice,
                    Terms = sponsorship.Terms,
                    TermsUrl = sponsorship.TermsUrl,
                    ExplicitTermAcceptance = sponsorship.ExplicitTermAcceptance,
                    TermsOfUseId = sponsorship.TermsOfUseId,
                    EventSponsorshipSaleableItems = new List<EventSponsorshipSaleableItem>(),
                    SponsorshipBenefits = new List<SponsorshipBenefit>()
                };

                foreach (var item in sponsorship.EventSponsorshipSaleableItems)
                {
                    newSponsorship.EventSponsorshipSaleableItems.Add(
                        new EventSponsorshipSaleableItem
                        {
                            SaleableItemId = item.SaleableItemId,
                            Price = item.Price,
                            Quantity = item.Quantity,
                            Description = item.Description,
                            HideOnInvoice = item.HideOnInvoice
                        });
                }

                foreach (var benefit in sponsorship.SponsorshipBenefits)
                {
                    var tmp = ((DbContext)_db).AttachOrGetLocal<IMzDB, SponsorshipBenefit>(x => x.SponsorshipBenefits,
                        new SponsorshipBenefit { SponsorshipBenefitId = benefit.SponsorshipBenefitId });

                    newSponsorship.SponsorshipBenefits.Add(tmp);
                }

                _db.EventSponsorships.Add(newSponsorship);
            }
        }

        private void CopyAttendeeRegistrationTypes(int eventDetailId, int existingEventId)
        {
            var registrationTypes = _db.Events.Where(x => x.EventId == existingEventId)
                .SelectMany(x => x.EventDetail.EventRegistrationTypes)
                .Include(x => x.EventRegistrationTypeSaleableItems)
                .Where(x => !x.IsDeleted);

            foreach (var type in registrationTypes)
            {
                var newType = new EventRegistrationType
                {
                    EventDetailId = eventDetailId,
                    Name = type.Name,
                    Description = type.Description,
                    Price = type.Price,
                    IsForMembers = type.IsForMembers,
                    IsForNonMembers = type.IsForNonMembers,
                    IsDisplayedForNonMembers = type.IsDisplayedForNonMembers,
                    MaximumQuantity = type.MaximumQuantity,
                    NumberOfAttendees = type.NumberOfAttendees,
                    ReserveAllAttendees = type.ReserveAllAttendees,
                    EventRegistrationPurchaseTypeId = type.EventRegistrationPurchaseTypeId,
                    AllowOnlinePurchase = type.AllowOnlinePurchase,
                    AllowOnlineInvoice = type.AllowOnlineInvoice,
                    Terms = type.Terms,
                    TermsUrl = type.TermsUrl,
                    ExplicitTermAcceptance = type.ExplicitTermAcceptance,
                    SystemEventRegistrationTypeId = type.SystemEventRegistrationTypeId,
                    // MZ-4480 - Moving payment gateway id from registration info to main event
                    // PaymentGatewayId = type.PaymentGatewayId,
                    EventRegistrationTypeSaleableItems = new List<EventRegistrationTypeSaleableItem>()
                };

                if (type.EventRegistrationTypeSaleableItems.Count > 0)
                {
                    foreach (var item in type.EventRegistrationTypeSaleableItems)
                    {
                        newType.EventRegistrationTypeSaleableItems.Add(
                            new EventRegistrationTypeSaleableItem
                            {
                                SaleableItemId = item.SaleableItemId,
                                Quantity = item.Quantity,
                                Price = item.Price,
                                HideOnInvoice = item.HideOnInvoice
                            });
                    }
                }

                _db.EventRegistrationTypes.Add(newType);
            }
        }

        private void CopyEventRegistrationInfo(int eventDetailId, int existingEventId)
        {
            var src = _db.Events
                .Where(x => x.EventId == existingEventId)
                .Select(x => x.EventDetail.EventRegistrationInfo)
                .SingleOrDefault();

            var dest = _db.EventRegistrationInfoes.SingleOrDefault(x => x.EventDetailId == eventDetailId) ?? new EventRegistrationInfo();

            dest.MaximumAttendees = src.MaximumAttendees;
            dest.AllowWaitingList = src.AllowWaitingList;
            dest.ShowRegisteredAttendeesPublically = src.ShowRegisteredAttendeesPublically;
            dest.ShowRegisteredAttendeesToMembers = src.ShowRegisteredAttendeesToMembers;

            // TODO: Clarify should we copy these fields?
            dest.AllowSponsorWaitingList = src.AllowSponsorWaitingList;
            dest.PaymentGatewayId = src.PaymentGatewayId;
            dest.AttendeeRegistrationTemplateId = src.AttendeeRegistrationTemplateId;
            dest.AttendeeReminderTemplateId = src.AttendeeReminderTemplateId;
            dest.SponsorRegistrationTemplateId = src.SponsorRegistrationTemplateId;
            dest.EnableRegistration = src.EnableRegistration;
            dest.EnableExhibitors = src.EnableExhibitors;
        }

        private void CopyEventDiscounts(int eventDetailId, int existingEventId)
        {
            var eventDiscounts = _db.Events.Where(x => x.EventId == existingEventId)
                .SelectMany(x => x.EventDetail.EventDiscounts)
                .Include(x => x.Discount)
                .Include(x => x.EventRegistrationType)
                .Where(x => !x.IsDeleted);

            foreach (var eventDiscount in eventDiscounts)
            {
                var newEventDiscount = new EventDiscount
                {
                    EventDetailId = eventDetailId,
                    EventRegistrationType = eventDiscount.EventRegistrationType != null
                        ? _db.EventRegistrationTypes.Local.Single(e => e.EventDetailId == eventDetailId && e.Name == eventDiscount.EventRegistrationType.Name)
                        : null,
                    CategoryItemId = eventDiscount.CategoryItemId,
                    MembershipTypeId = eventDiscount.MembershipTypeId,
                    GroupId = eventDiscount.GroupId,
                    IsExcluded = eventDiscount.IsExcluded,
                    Discount = new Discount
                    {
                        Name = eventDiscount.Discount.Name,
                        DiscountTypeId = eventDiscount.Discount.DiscountTypeId,
                        StartDate = eventDiscount.Discount.StartDate,
                        EndDate = eventDiscount.Discount.EndDate,
                        PromoCode = eventDiscount.Discount.PromoCode,
                        CanBeUsedWithOtherDiscounts = eventDiscount.Discount.CanBeUsedWithOtherDiscounts,
                        TotalAvailable = eventDiscount.Discount.TotalAvailable,
                        LimitPerPurchase = eventDiscount.Discount.LimitPerPurchase,
                        MinToActivateDiscount = eventDiscount.Discount.MinToActivateDiscount,
                        UsedQuantity = 0/*eventDiscount.Discount.UsedQuantity*/,
                        DiscountNewPrice = eventDiscount.Discount.DiscountNewPrice,
                        DiscountPercent = eventDiscount.Discount.DiscountPercent,
                        DiscountAmount = eventDiscount.Discount.DiscountAmount
                    }
                };

                _db.EventDiscounts.Add(newEventDiscount);
                _db.Discounts.Add(newEventDiscount.Discount);
            }
        }

        private void CopyEventTasks(int eventDetailId, int existingEventId)
        {
            var eventTaskItems = _db.EventTaskItems.Where(x => x.EventId == existingEventId);

            if (eventTaskItems != null)
            {
                var taskItems = _db.TaskItems.Where(x => eventTaskItems.Select(y => y.TaskItemId).Contains(x.TaskItemId));

                taskItems.ForEach(task =>
                {
                    var taskItem = new TaskItem
                    {
                        TenantId = _user.TenantId,
                        ProjectId = task.ProjectId,
                        Name = task.Name,
                        Description = task.Description,
                        TaskPriorityId = task.TaskPriorityId,
                        SystemTaskTypeId = task.SystemTaskTypeId,
                        TaskTypeId = task.TaskTypeId,
                        StartDate = task.StartDate,
                        DueDate = task.DueDate,
                        AssignedToContactId = task.AssignedToContactId,
                        SecondaryContactId = task.SecondaryContactId,
                        ContactId = task.ContactId,
                        EstimatedHours = task.EstimatedHours,
                        DependentOnTaskId = task.DependentOnTaskId,
                        DependentOnMustCompleteFirst = task.DependentOnMustCompleteFirst,
                        ProjectMilestoneId = task.ProjectMilestoneId,
                        DependentsMustCompleteFirst = task.DependentsMustCompleteFirst,
                        TaskItemInfoJson = task.TaskItemInfoJson,
                        AuditId = task.AuditId,
                        TaskData = task.TaskData,
                        ForumTopicId = task.ForumTopicId,
                        DueTime = task.DueTime,
                        IsDeleted = task.IsDeleted
                    };

                    _db.TaskItems.Add(taskItem);
                    _db.SaveChanges();

                    _db.EventTaskItems.Add(new EventTaskItem
                    {
                        EventId = eventDetailId,
                        TaskItemId = taskItem.TaskItemId
                    });
                });
            }
        }

        private void CopyEventExhibitors(int eventDetailId, int existingEventId)
        {
            var exhibitors = _db.EventExhibitors.Where(x => x.EventDetailId == existingEventId);

            foreach (var exh in exhibitors)
            {
                var exhibitor = new EventExhibitor
                {
                    EventDetailId = eventDetailId,
                    ExhibitorStatusId = exh.ExhibitorStatusId,
                    EventExhibitorTypeId = exh.EventExhibitorTypeId,
                    EventSponsorId = exh.EventSponsorId,
                    DepositPurchaseId = exh.DepositPurchaseId,
                    PurchaseId = exh.PurchaseId,
                    TenantId = exh.TenantId,
                    AuditId = exh.AuditId,
                    IsDeleted = exh.IsDeleted,
                    ContactId = exh.ContactId,
                    PrimaryContactId = exh.PrimaryContactId,
                    Description = exh.Description,
                    Comments = exh.Comments,
                    PreferredBoothInfo = exh.PreferredBoothInfo,
                    PreviousExhibitorYears = exh.PreviousExhibitorYears,
                    ExhibitorCapacity = exh.ExhibitorCapacity,
                    AddressId = exh.AddressId,
                    WebAddressId = exh.WebAddressId,
                    PhoneId = exh.PhoneId,
                    EmailAddressId = exh.EmailAddressId,
                    RegistrationDate = exh.RegistrationDate,
                    ImageFileId = exh.ImageFileId,
                    DisplayNameOverride = exh.DisplayNameOverride
                };

                _db.EventExhibitors.Add(exhibitor);
            }
        }

        private void CopyEventExhibitorSetup(int eventDetailId, int existingEventId)
        {
            var eventExhibitor = _db.Events.Where(x => x.EventId == existingEventId)
               .Include(x => x.EventDetail.EventExhibitorTypes)
               .Include(x => x.EventDetail.EventExhibitorRegistrationInfo)
               .SingleOrDefault();

            if (eventExhibitor.EventDetail.EventExhibitorRegistrationInfo != null)
            {
                var existingEventExhibitorRegInfo = eventExhibitor.EventDetail.EventExhibitorRegistrationInfo;
                var eventExhibitorRegistrationInfo = new EventExhibitorRegistrationInfo
                {
                    EventDetailId = eventDetailId,
                    DefaultBoothConfigurationId = existingEventExhibitorRegInfo.DefaultBoothConfigurationId,
                    RegistrationStartDate = existingEventExhibitorRegInfo.RegistrationStartDate,
                    RegistrationEndDate = existingEventExhibitorRegInfo.RegistrationEndDate,
                    AllowWaitingList = existingEventExhibitorRegInfo.AllowWaitingList,
                    DepositDueBy = existingEventExhibitorRegInfo.DepositDueBy,
                    EnableOnlineExhibitorRegistration = existingEventExhibitorRegInfo.EnableOnlineExhibitorRegistration,
                    ExhibitorDirectoryId = existingEventExhibitorRegInfo.ExhibitorDirectoryId,
                    TermsOfUseId = existingEventExhibitorRegInfo.TermsOfUseId,
                    AllowLogo = existingEventExhibitorRegInfo.AllowLogo,
                    LogoWidth = existingEventExhibitorRegInfo.LogoWidth,
                    LogoHeight = existingEventExhibitorRegInfo.LogoHeight,
                    RegistrationInstructions = existingEventExhibitorRegInfo.RegistrationInstructions,
                    ConfirmationMessage = existingEventExhibitorRegInfo.ConfirmationMessage,
                    MaximumExhibitors = existingEventExhibitorRegInfo.MaximumExhibitors,
                    AllowInvoicing = existingEventExhibitorRegInfo.AllowInvoicing
                };

                _db.EventExhibitorRegistrationInfoes.Add(eventExhibitorRegistrationInfo);
            }

            eventExhibitor.EventDetail.EventExhibitorTypes.ForEach(x =>
            {
                var eventExhibitorType = new EventExhibitorType
                {
                    EventDetailId = eventDetailId,
                    Name = x.Name,
                    Description = x.Description,
                    TenantId = _user.TenantId,
                    IncludedAttendeeQuantity = x.IncludedAttendeeQuantity,
                    ExhibitorAttendeeEventRegistrationTypeId = x.ExhibitorAttendeeEventRegistrationTypeId,
                    MaximumExhibitors = x.MaximumExhibitors
                };

                _db.EventExhibitorTypes.Add(eventExhibitorType);
            });
        }

        private void CopyEventAttendeeSetup(IMzDB db, int eventDetailId, int existingEventId)
        {
            var src = db.Events
                .Where(x => x.EventId == existingEventId)
                .Select(x => x.EventDetail.EventRegistrationInfo)
                .SingleOrDefault();

            var dest = db.EventRegistrationInfoes.SingleOrDefault(x => x.EventDetailId == eventDetailId) ?? new EventRegistrationInfo();

            dest.RegistrationStartDate = src.RegistrationStartDate;
            dest.RegistrationEndDate = src.RegistrationEndDate;
            dest.AllowWaitingList = src.AllowWaitingList;
            dest.ShowRegisteredAttendeesPublically = src.ShowRegisteredAttendeesPublically;
            dest.MaximumAttendees = src.MaximumAttendees;
            dest.ShowRegisteredAttendeesToMembers = src.ShowRegisteredAttendeesToMembers;
            dest.ExternalRegistrationLink = src.ExternalRegistrationLink;
            dest.SponsorRegistrationStartDate = src.SponsorRegistrationStartDate;
            dest.SponsorRegistrationEndDate = src.SponsorRegistrationEndDate;
            dest.AllowSponsorWaitingList = src.AllowSponsorWaitingList;
            dest.PaymentGatewayId = src.PaymentGatewayId;
            dest.AttendeeRegistrationTemplateId = src.AttendeeRegistrationTemplateId;
            dest.AttendeeReminderTemplateId = src.AttendeeReminderTemplateId;
            dest.SponsorRegistrationTemplateId = src.SponsorRegistrationTemplateId;
            dest.EnableRegistration = src.EnableRegistration;
            dest.EnableSessions = src.EnableSessions;
            dest.EnableExhibitors = src.EnableExhibitors;
            dest.AttendeeRegistrationInstructions = src.AttendeeRegistrationInstructions;
            dest.SponsorRegistrationInstructions = src.SponsorRegistrationInstructions;
            dest.AdditionalEventConfirmationMessage = src.AdditionalEventConfirmationMessage;
            dest.FieldRequirementsJson = src.FieldRequirementsJson;
            dest.SessionChangesUntilDateTime = src.SessionChangesUntilDateTime;
            dest.FullCancelUntilDateTime = src.FullCancelUntilDateTime;
            dest.CancelPolicyDescription = src.CancelPolicyDescription;
            dest.CancelPolicyJson = src.CancelPolicyJson;
            dest.AllowAttendeeSubstitutesUntilDate = src.AllowAttendeeSubstitutesUntilDate;
            dest.EnableSponsors = src.EnableSponsors;
        }

        private void CopyEventAttendee(IMzDB db, int eventDetailId, int existingEventId)
        {
            var eventInfo = db.Events.Where(x => x.EventId == existingEventId)
              .Include(x => x.EventRegistrations)
              .Include(x => x.EventAttendees)
              .SingleOrDefault();

            eventInfo.EventRegistrations.ForEach(eventReg =>
            {
                var eventAttendee = eventInfo.EventAttendees.SingleOrDefault(x => x.EventRegistrationId == eventReg.EventRegistrationId);

                if (eventAttendee != null)
                {
                    var registration = new EventRegistration
                    {
                        EventRegistrationTypeId = eventReg.EventRegistrationTypeId,
                        RegistrationKey = Guid.NewGuid(),
                        EventId = eventDetailId,
                        TenantId = _user.TenantId,
                        RegistrationDate = DateTime.UtcNow,
                        RegisteredBy = _user.ContactId,
                        PurchaseStatusTypeId = eventReg.PurchaseStatusTypeId,
                        WebReferralSourceTypeId = eventReg.WebReferralSourceTypeId,
                        Comments = eventReg.Comments,
                        WebMarketingInfoId = eventReg.WebMarketingInfoId,
                        TableNumber = eventReg.TableNumber
                    };

                    db.EventRegistrations.Add(registration);
                    db.SaveChanges();

                    var attendee = new EventAttendee
                    {
                        EventRegistrationId = registration.EventRegistrationId,
                        EventAttendeeStatusId = eventAttendee.EventAttendeeStatusId,
                        EventId = eventDetailId,
                        ContactId = eventAttendee.ContactId,
                        OrganizationId = eventAttendee.OrganizationId,
                        AddressId = eventAttendee.AddressId,
                        EmailId = eventAttendee.EmailId,
                        PhoneId = eventAttendee.PhoneId,
                        SeatNumber = eventAttendee.SeatNumber
                    };

                    db.EventAttendees.Add(attendee);
                }
            });
        }

        private MzDB GetMzDb()
        {
            var connStringStore = _container.GetInstance<IConnectionStringStore>();
            return new MzDB(connStringStore.GetConnectionString(_user.TenantKey));
        }
    }
}

