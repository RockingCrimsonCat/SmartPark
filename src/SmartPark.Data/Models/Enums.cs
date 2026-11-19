namespace SmartPark.Data.Models;

public enum ParkingStatus { Draft, Active, Archived }

public enum SpotType { Regular, Electric, Disabled }

public enum SpotStatus { Free, Occupied, Reserved, Blocked, Maintenance }

public enum SessionStatus { Active, Closed, AwaitingPayment, Unpaid }

public enum EntryMode { Guest, Booking, Subscription, Manual, Offline }

public enum BookingStatus { PendingPayment, Confirmed, Activated, Completed, Cancelled, Expired }

public enum PaymentPurpose { Session, Booking, Subscription }

public enum PaymentStatus { Succeeded, Failed, Refunded }

public enum DeviceType { Camera, Barrier, Sensor, Display, Gateway }

public enum DeviceStatus { Active, Maintenance, Faulty, Retired }

public enum CommandStatus { Pending, Delivered }

public enum IncidentStatus { New, InProgress, Resolved, Closed }

public enum IncidentPriority { Low, Medium, High, Critical }
