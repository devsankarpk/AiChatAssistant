import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { environment } from '../../../environments/environment';
import { UsageComponent } from './usage.component';

describe('UsageComponent', () => {
  let component: UsageComponent;
  let fixture: ComponentFixture<UsageComponent>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [UsageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);

    fixture = TestBed.createComponent(UsageComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    httpMock
      .expectOne((req) => req.url === `${environment.apiBaseUrl}/admin/usage`)
      .flush({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 });
  });

  afterEach(() => {
    // the totals requests that go out with the page
    httpMock.match((req) => req.url === `${environment.apiBaseUrl}/admin/usage/summary`).forEach((r) => r.flush({ totals: { requests: 0, tokens: 0, promptTokens: 0, completionTokens: 0, cost: 0 }, byUser: [], byDay: [] }));
    httpMock.verify();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('resets to page 1 when a filter is applied', () => {
    component.page.set(3);
    component.applyFilter();
    expect(component.page()).toBe(1);
    httpMock.expectOne((req) => req.url === `${environment.apiBaseUrl}/admin/usage`).flush({
      items: [],
      page: 1,
      pageSize: 20,
      totalCount: 0,
      totalPages: 0,
    });
  });
});
