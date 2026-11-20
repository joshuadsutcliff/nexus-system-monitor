#![allow(non_camel_case_types, non_upper_case_globals)]

use core_foundation_sys::array::{CFArrayGetCount, CFArrayGetValueAtIndex, CFArrayRef};
use core_foundation_sys::base::{CFAllocatorRef, CFRelease, CFTypeRef, kCFAllocatorDefault};
use core_foundation_sys::dictionary::{
    CFDictionaryGetValue, CFDictionaryRef, CFMutableDictionaryRef,
};
use core_foundation_sys::number::{
    CFNumberGetValue, CFNumberRef, kCFNumberFloat64Type, kCFNumberSInt64Type,
};
use core_foundation_sys::string::{
    CFStringCreateWithCString, CFStringGetCString, CFStringRef, kCFStringEncodingUTF8,
};
use std::ffi::{CStr, CString, c_char, c_int, c_void};
use std::ptr;

pub type io_object_t = u32;
pub type io_iterator_t = io_object_t;
pub type io_registry_entry_t = io_object_t;
pub type io_service_t = io_object_t;
pub type io_connect_t = io_object_t;
pub type kern_return_t = c_int;

pub const KERN_SUCCESS: kern_return_t = 0;
pub const kIOMainPortDefault: u32 = 0;

#[link(name = "IOKit", kind = "framework")]
unsafe extern "C" {
    pub fn IOServiceMatching(name: *const c_char) -> CFMutableDictionaryRef;
    pub fn IOServiceGetMatchingServices(
        mainPort: u32,
        matching: CFDictionaryRef,
        existing: *mut io_iterator_t,
    ) -> kern_return_t;
    pub fn IOServiceGetMatchingService(mainPort: u32, matching: CFDictionaryRef) -> io_service_t;
    pub fn IOIteratorNext(iterator: io_iterator_t) -> io_object_t;
    pub fn IOObjectRelease(object: io_object_t) -> kern_return_t;
    pub fn IORegistryEntryCreateCFProperties(
        entry: io_registry_entry_t,
        properties: *mut CFMutableDictionaryRef,
        allocator: CFAllocatorRef,
        options: u32,
    ) -> kern_return_t;
    pub fn IOServiceOpen(
        service: io_service_t,
        owningTask: u32,
        connect_type: u32,
        connect: *mut io_connect_t,
    ) -> kern_return_t;
    pub fn IOServiceClose(connect: io_connect_t) -> kern_return_t;
    pub fn IOConnectCallStructMethod(
        connection: u32,
        selector: u32,
        inputStruct: *const c_void,
        inputStructCnt: usize,
        outputStruct: *mut c_void,
        outputStructCnt: *mut usize,
    ) -> kern_return_t;
    pub fn IOPMGetThermalWarningLevel(thermalLevel: *mut u32) -> kern_return_t;
}

unsafe extern "C" {
    pub fn mach_host_self() -> u32;
    pub fn vm_deallocate(target_task: u32, address: usize, size: usize) -> kern_return_t;
    pub fn notify_register_check(name: *const c_char, out_token: *mut c_int) -> u32;
    pub fn notify_get_state(token: c_int, state64: *mut u64) -> u32;
    pub fn notify_cancel(token: c_int) -> u32;
}

/// RAII wrapper for `CFTypeRef` pointers that must be released via `CFRelease`.
pub struct CfGuard(pub CFTypeRef);

impl Drop for CfGuard {
    fn drop(&mut self) {
        if !self.0.is_null() {
            // SAFETY: `self.0` is a valid non-null CoreFoundation object owned by this guard.
            unsafe {
                CFRelease(self.0);
            }
        }
    }
}

/// RAII wrapper for IOKit `io_object_t` handles.
pub struct IoObjectGuard(pub io_object_t);

impl Drop for IoObjectGuard {
    fn drop(&mut self) {
        if self.0 != 0 {
            // SAFETY: `self.0` is a non-zero IOKit object handle returned by IOKit APIs.
            unsafe {
                let _ = IOObjectRelease(self.0);
            }
        }
    }
}

/// Creates a `CFStringRef` wrapped in `CfGuard` from a Rust `&str`.
pub fn make_cfstring(s: &str) -> Option<CfGuard> {
    let cstr = CString::new(s).ok()?;
    // SAFETY: `cstr.as_ptr()` is a valid NUL-terminated UTF-8 string.
    let cf_str = unsafe {
        CFStringCreateWithCString(kCFAllocatorDefault, cstr.as_ptr(), kCFStringEncodingUTF8)
    };
    if cf_str.is_null() {
        None
    } else {
        Some(CfGuard(cf_str as CFTypeRef))
    }
}

/// Converts a `CFStringRef` into a Rust `String`.
pub(crate) fn cfstring_to_string(cf_str: CFStringRef) -> Option<String> {
    if cf_str.is_null() {
        return None;
    }
    let mut buf = [0 as c_char; 256];
    // SAFETY: `cf_str` is non-null and `buf` has 256 bytes of writable storage.
    let ok = unsafe {
        CFStringGetCString(
            cf_str,
            buf.as_mut_ptr(),
            buf.len() as isize,
            kCFStringEncodingUTF8,
        )
    };
    if ok == 0 {
        return None;
    }
    // SAFETY: `CFStringGetCString` succeeded and wrote a NUL-terminated C string into `buf`.
    let c_str = unsafe { CStr::from_ptr(buf.as_ptr()) };
    Some(c_str.to_string_lossy().into_owned())
}

/// Reads an `i64` from a `CFDictionaryRef` by string key.
pub(crate) fn dict_get_i64(dict: CFDictionaryRef, key: &str) -> Option<i64> {
    if dict.is_null() {
        return None;
    }
    let cf_key = make_cfstring(key)?;
    // SAFETY: `dict` and `cf_key.0` are valid CoreFoundation pointers.
    let val_ptr = unsafe { CFDictionaryGetValue(dict, cf_key.0) };
    if val_ptr.is_null() {
        return None;
    }
    let mut out: i64 = 0;
    // SAFETY: `val_ptr` is read as a `CFNumberRef` into `out`.
    let ok = unsafe {
        CFNumberGetValue(
            val_ptr as CFNumberRef,
            kCFNumberSInt64Type,
            (&raw mut out).cast::<c_void>(),
        )
    };
    if ok { Some(out) } else { None }
}

/// Reads an `f64` from a `CFDictionaryRef` by string key.
#[allow(dead_code)]
pub(crate) fn dict_get_f64(dict: CFDictionaryRef, key: &str) -> Option<f64> {
    if dict.is_null() {
        return None;
    }
    let cf_key = make_cfstring(key)?;
    // SAFETY: `dict` and `cf_key.0` are valid CoreFoundation pointers.
    let val_ptr = unsafe { CFDictionaryGetValue(dict, cf_key.0) };
    if val_ptr.is_null() {
        return None;
    }
    let mut out: f64 = 0.0;
    // SAFETY: `val_ptr` is read as a `CFNumberRef` into `out`.
    let ok = unsafe {
        CFNumberGetValue(
            val_ptr as CFNumberRef,
            kCFNumberFloat64Type,
            (&raw mut out).cast::<c_void>(),
        )
    };
    if ok { Some(out) } else { None }
}

/// Reads a nested `CFDictionaryRef` from `dict` by string key (unowned borrow).
pub(crate) fn dict_get_dict(dict: CFDictionaryRef, key: &str) -> Option<CFDictionaryRef> {
    if dict.is_null() {
        return None;
    }
    let cf_key = make_cfstring(key)?;
    // SAFETY: `dict` and `cf_key.0` are valid CoreFoundation pointers.
    let val_ptr = unsafe { CFDictionaryGetValue(dict, cf_key.0) };
    if val_ptr.is_null() {
        None
    } else {
        Some(val_ptr as CFDictionaryRef)
    }
}

/// Reads a nested `CFArrayRef` from `dict` by string key (unowned borrow).
pub(crate) fn dict_get_array(dict: CFDictionaryRef, key: &str) -> Option<CFArrayRef> {
    if dict.is_null() {
        return None;
    }
    let cf_key = make_cfstring(key)?;
    // SAFETY: `dict` and `cf_key.0` are valid CoreFoundation pointers.
    let val_ptr = unsafe { CFDictionaryGetValue(dict, cf_key.0) };
    if val_ptr.is_null() {
        None
    } else {
        Some(val_ptr as CFArrayRef)
    }
}

/// Reads a `String` from `dict` by string key.
pub(crate) fn dict_get_string(dict: CFDictionaryRef, key: &str) -> Option<String> {
    if dict.is_null() {
        return None;
    }
    let cf_key = make_cfstring(key)?;
    // SAFETY: `dict` and `cf_key.0` are valid CoreFoundation pointers.
    let val_ptr = unsafe { CFDictionaryGetValue(dict, cf_key.0) };
    if val_ptr.is_null() {
        None
    } else {
        cfstring_to_string(val_ptr as CFStringRef)
    }
}

/// Returns the number of elements in a `CFArrayRef`.
pub(crate) fn array_len(arr: CFArrayRef) -> isize {
    if arr.is_null() {
        0
    } else {
        // SAFETY: `arr` is non-null.
        unsafe { CFArrayGetCount(arr) }
    }
}

/// Returns the pointer at `idx` in a `CFArrayRef`.
pub(crate) fn array_get(arr: CFArrayRef, idx: isize) -> *const c_void {
    if arr.is_null() {
        ptr::null()
    } else {
        // SAFETY: Caller bounds-checks `idx` against `array_len(arr)`.
        unsafe { CFArrayGetValueAtIndex(arr, idx) }
    }
}

/// Reads a `u32` sysctl value by name.
pub fn sysctl_u32(name: &str) -> Option<u32> {
    let cname = CString::new(name).ok()?;
    let mut val: u32 = 0;
    let mut size = std::mem::size_of::<u32>();
    // SAFETY: `cname` is a valid C string and `val` has `size` bytes of storage.
    let rc = unsafe {
        libc::sysctlbyname(
            cname.as_ptr(),
            (&raw mut val).cast::<c_void>(),
            &raw mut size,
            ptr::null_mut(),
            0,
        )
    };
    if rc == 0 && size == std::mem::size_of::<u32>() {
        Some(val)
    } else {
        None
    }
}

/// Reads a `u64` sysctl value by name.
pub fn sysctl_u64(name: &str) -> Option<u64> {
    let cname = CString::new(name).ok()?;
    let mut val: u64 = 0;
    let mut size = std::mem::size_of::<u64>();
    // SAFETY: `cname` is a valid C string and `val` has `size` bytes of storage.
    let rc = unsafe {
        libc::sysctlbyname(
            cname.as_ptr(),
            (&raw mut val).cast::<c_void>(),
            &raw mut size,
            ptr::null_mut(),
            0,
        )
    };
    if rc == 0 && size == std::mem::size_of::<u64>() {
        Some(val)
    } else {
        None
    }
}

/// AppleSMC 80-byte request/response struct (`0x1005db9c0`).
#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct SmcVersion {
    pub major: u8,
    pub minor: u8,
    pub build: u8,
    pub reserved: u8,
    pub release: u16,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct SmcPLimitData {
    pub version: u16,
    pub length: u16,
    pub cpu_p_limit: u32,
    pub gpu_p_limit: u32,
    pub mem_p_limit: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct SmcKeyInfoData {
    pub data_size: u32,
    pub data_type: u32,
    pub data_attributes: u8,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct SmcKeyData {
    pub key: u32,
    pub vers: SmcVersion,
    pub p_limit_data: SmcPLimitData,
    pub key_info: SmcKeyInfoData,
    pub result: u8,
    pub status: u8,
    pub data8: u8,
    pub data32: u32,
    pub bytes: [u8; 32],
}
